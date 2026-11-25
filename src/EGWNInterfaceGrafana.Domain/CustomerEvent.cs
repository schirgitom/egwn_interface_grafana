namespace EGWNInterfaceGrafana.Domain;

public sealed record CustomerEvent(
    Guid EventId, string EventType, int SchemaVersion,
    DateTimeOffset OccurredAtUtc, CustomerSnapshot Customer);

public sealed record CustomerSnapshot(
    Guid Id, string CustomerNumber, string Name, string? Email, bool IsDeleted,
    string? RegistrationCode = null,
    IReadOnlyList<MeteringPoint>? MeteringPoints = null,
    IReadOnlyList<EnergyCommunity>? EnergyCommunities = null);

public sealed record MeteringPoint(
    Guid Id, string MeteringPointCode, string EnergyDirection, string? Description);

public sealed record CommunityMeteringPoint(
    Guid Id, string MeteringPointCode, string EnergyDirection,
    string? Description, decimal ParticipationPercentage);

public sealed record EnergyCommunity(
    Guid Id, string Name, string? Source, string? SourceId,
    IReadOnlyList<CommunityMeteringPoint> MeteringPoints, string? LogoUrl = null);

public sealed class CustomerState
{
    public Guid Id { get; set; }
    public Guid LastEventId { get; set; }
    public DateTimeOffset LastOccurredAtUtc { get; set; }
    public string SnapshotJson { get; set; } = "";
    public string? CodeHash { get; set; }
    public long? OrganizationId { get; set; }
    public bool Ready { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class ProcessedEvent
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
    public string Fingerprint { get; set; } = "";
    public bool Completed { get; set; }
}

public sealed class UserMembership
{
    public string UserId { get; set; } = "";
    public Guid CustomerId { get; set; }
}
