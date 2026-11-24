using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EGWNInterfaceGrafana.Domain;

namespace EGWNInterfaceGrafana.Application;

public static class EventPolicy
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CustomerEvent Parse(ReadOnlySpan<byte> body)
    {
        CustomerEvent? message;
        try { message = JsonSerializer.Deserialize<CustomerEvent>(body, Json); }
        catch (JsonException) { throw new InvalidEventException("Invalid event JSON."); }
        if (message is null) throw new InvalidEventException("Missing event.");
        if (message.Customer is { } customer)
            message = message with { Customer = customer with { RegistrationCode = null } };
        message = message with { OccurredAtUtc = message.OccurredAtUtc.ToUniversalTime() };
        Validate(message);
        return message;
    }

    public static void Validate(CustomerEvent message)
    {
        if (message.SchemaVersion != 1 || message.EventId == Guid.Empty ||
            message.OccurredAtUtc == default || message.OccurredAtUtc.Offset != TimeSpan.Zero ||
            message.Customer is not { Id: var id } || id == Guid.Empty ||
            message.EventType is not ("customer.created" or "customer.updated" or "customer.deleted"))
            throw new InvalidEventException("Unsupported or incomplete envelope.");

        var customer = message.Customer;
        if (customer.IsDeleted != (message.EventType == "customer.deleted") ||
            string.IsNullOrWhiteSpace(customer.CustomerNumber) || string.IsNullOrWhiteSpace(customer.Name))
            throw new InvalidEventException("Invalid customer.");
        if (!customer.IsDeleted && customer.RegistrationCode is { } code &&
            (code.Length is < 32 or > 512 || code.Any(char.IsWhiteSpace)))
            throw new InvalidEventException("Registration code must be a high-entropy secret of 32 to 512 characters.");
        if (customer.MeteringPoints is { } points &&
            (points.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.MeteringPointCode) ||
                             string.IsNullOrWhiteSpace(p.EnergyDirection)) ||
             points.Select(p => p.Id).Distinct().Count() != points.Count))
            throw new InvalidEventException("Invalid metering points.");
        if (customer.EnergyCommunities is { } communities &&
            (communities.Any(c => c is null || c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name) ||
                c.MeteringPoints is null ||
                c.MeteringPoints.Any(p => p is null || p.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(p.MeteringPointCode) || string.IsNullOrWhiteSpace(p.EnergyDirection) ||
                    p.ParticipationPercentage is < 0 or > 100) ||
                c.MeteringPoints.Select(p => p.Id).Distinct().Count() != c.MeteringPoints.Count) ||
             communities.Select(c => c.Id).Distinct().Count() != communities.Count))
            throw new InvalidEventException("Invalid communities.");
    }

    public static CustomerSnapshot Merge(CustomerSnapshot incoming, CustomerSnapshot? previous) =>
        incoming with
        {
            RegistrationCode = null,
            MeteringPoints = incoming.IsDeleted ? [] : incoming.MeteringPoints ?? previous?.MeteringPoints ?? [],
            EnergyCommunities = incoming.IsDeleted ? [] : incoming.EnergyCommunities ?? previous?.EnergyCommunities ?? []
        };

    public static string Fingerprint(CustomerEvent message, string? codeHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { Event = message with { Customer = message.Customer with { RegistrationCode = null } }, CodeHash = codeHash }, Json))));
}
