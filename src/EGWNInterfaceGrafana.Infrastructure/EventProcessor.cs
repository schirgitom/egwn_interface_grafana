using System.Text.Json;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed class EventProcessor(
    ServiceDbContext db, IGrafanaProvisioner grafana, IIdentityRevoker identities,
    RegistrationSecrets secrets, TimeProvider time, ILogger<EventProcessor> logger)
{
    public async Task ProcessAsync(CustomerEvent message, CancellationToken ct)
    {
        EventPolicy.Validate(message);
        // PostgreSQL timestamps have microsecond precision, unlike .NET's 100 ns ticks.
        message = message with { OccurredAtUtc = new DateTimeOffset(
            message.OccurredAtUtc.Ticks - message.OccurredAtUtc.Ticks % 10, TimeSpan.Zero) };
        var incoming = message.Customer;
        var codeHash = incoming.RegistrationCode is { } code ? secrets.Hash(code) : null;
        var fingerprint = EventPolicy.Fingerprint(message, codeHash);
        // Persist desired state before remote work. Failed deletion/provisioning must remain fail-closed.
        await using (var staging = await db.LockCustomerAsync(incoming.Id, ct))
        {
            var processed = await db.Events.FindAsync([message.EventId], ct);
            if (processed is not null)
            {
                if (processed.CustomerId != incoming.Id || processed.Fingerprint != fingerprint)
                    throw new InvalidEventException("Event ID reused with different content.");
                if (processed.Completed) return;
            }
            else
            {
                var desired = await db.Customers.FindAsync([incoming.Id], ct);
                if (desired is not null && message.OccurredAtUtc == desired.LastOccurredAtUtc)
                    throw new InvalidEventException("Conflicting timestamp; source revision required.");
                var stale = desired is not null && message.OccurredAtUtc < desired.LastOccurredAtUtc;
                if (!stale)
                {
                    if (desired is { IsDeleted: true } && !incoming.IsDeleted)
                        throw new InvalidEventException("A deleted customer ID cannot be reused.");
                    var previous = desired is null ? null :
                        JsonSerializer.Deserialize<CustomerSnapshot>(desired.SnapshotJson, EventPolicy.Json);
                    if (previous is not null && previous.CustomerNumber != incoming.CustomerNumber)
                        throw new InvalidEventException("Customer number is immutable.");
                    if (desired is null)
                    {
                        desired = new CustomerState { Id = incoming.Id };
                        db.Customers.Add(desired);
                    }
                    desired.Ready = false;
                    desired.IsDeleted = incoming.IsDeleted;
                    desired.CodeHash = incoming.IsDeleted ? null : codeHash ?? desired.CodeHash;
                    desired.SnapshotJson = JsonSerializer.Serialize(EventPolicy.Merge(incoming, previous), EventPolicy.Json);
                    desired.LastOccurredAtUtc = message.OccurredAtUtc;
                    desired.LastEventId = message.EventId;
                    if (desired.CodeHash is { } activeHash &&
                        await db.Customers.AnyAsync(c => c.Id != desired.Id && c.CodeHash == activeHash, ct))
                        throw new InvalidEventException("Registration code belongs to another customer.");
                }
                else logger.LogWarning("Ignoring stale event {EventId} for customer {CustomerId}", message.EventId, incoming.Id);
                db.Events.Add(new ProcessedEvent
                {
                    Id = message.EventId, CustomerId = incoming.Id, Fingerprint = fingerprint,
                    ProcessedAtUtc = time.GetUtcNow(), Completed = stale
                });
            }
            await db.SaveChangesAsync(ct);
            await staging.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        await using var transaction = await db.LockCustomerAsync(incoming.Id, ct);
        var current = await db.Customers.SingleAsync(c => c.Id == incoming.Id, ct);
        var entry = await db.Events.SingleAsync(e => e.Id == message.EventId, ct);
        if (entry.Completed) return;
        if (current.LastEventId == message.EventId)
        {
            if (current.IsDeleted)
            {
                await grafana.DeleteAsync(current.Id, current.OrganizationId, ct);
                foreach (var user in await db.Memberships.Where(m => m.CustomerId == current.Id).ToListAsync(ct))
                    await identities.RevokeAsync(user.UserId, ct);
                current.OrganizationId = null;
            }
            else
            {
                var snapshot = JsonSerializer.Deserialize<CustomerSnapshot>(current.SnapshotJson, EventPolicy.Json)!;
                current.OrganizationId = await grafana.ReconcileAsync(snapshot, current.OrganizationId, ct);
                current.Ready = true;
            }
        }
        entry.Completed = true;
        entry.ProcessedAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Processed event {EventId} for customer {CustomerId}", message.EventId, incoming.Id);
    }
}
