using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed record RegistrationResolution(string Ticket, string Organization);
public sealed record MembershipResult(Guid CustomerId, string Organization);

public sealed class RegistrationService(
    ServiceDbContext db, RegistrationSecrets secrets, ILogger<RegistrationService> logger)
{
    public async Task<RegistrationResolution> ResolveAsync(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length is < 32 or > 512)
            throw new RegistrationDeniedException();
        var hash = secrets.Hash(code);
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(
            c => c.CodeHash == hash && c.Ready && !c.IsDeleted, ct);
        if (customer is null) throw new RegistrationDeniedException();
        return new(secrets.CreateTicket(customer.Id, hash), ResourceIdentity.Organization(customer.Id));
    }

    public async Task<MembershipResult> AssignAsync(string ticketValue, string userId, CancellationToken ct)
    {
        ValidateUserId(userId);
        var ticket = secrets.ReadTicket(ticketValue);
        await using var transaction = await db.LockCustomerAsync(ticket.CustomerId, ct);
        // All assignments for the same immutable user serialize even if they target different customers.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({userId}, 1))", ct);
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == ticket.CustomerId, ct);
        if (customer is not { Ready: true, IsDeleted: false } || customer.CodeHash != ticket.CodeHash)
            throw new RegistrationDeniedException();
        var existing = await db.Memberships.FindAsync([userId], ct);
        if (existing is not null && existing.CustomerId != customer.Id)
            throw new RegistrationDeniedException();
        if (existing is null)
            db.Memberships.Add(new UserMembership { UserId = userId, CustomerId = customer.Id });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Customer membership confirmed for customer {CustomerId}", customer.Id);
        return new(customer.Id, ResourceIdentity.Organization(customer.Id));
    }

    public async Task<MembershipResult> MembershipAsync(string userId, CancellationToken ct)
    {
        ValidateUserId(userId);
        var membership = await db.Memberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId, ct);
        if (membership is null) throw new RegistrationDeniedException();
        await using var transaction = await db.LockCustomerAsync(membership.CustomerId, ct);
        if (!await db.Customers.AnyAsync(c => c.Id == membership.CustomerId && c.Ready && !c.IsDeleted, ct))
            throw new RegistrationDeniedException();
        return new(membership.CustomerId, ResourceIdentity.Organization(membership.CustomerId));
    }

    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 128)
            throw new RegistrationDeniedException();
    }
}
