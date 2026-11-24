using System.Text;
using System.Text.Json;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using EGWNInterfaceGrafana.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace EGWNInterfaceGrafana.Tests;

public sealed class PolicyTests
{
    internal const string OriginalCustomerEventJson = """{"eventId":"55e7fc4a-29fe-0ec5-3a0a-821deb2211a3","eventType":"customer.created","schemaVersion":1,"occurredAtUtc":"2026-10-09T02:00:00+02:00","customer":{"id":"55e7fc4a-29fe-0ec5-3a0a-821deb2211a3","customerNumber":"232","name":"Wiener Johann","email":null,"isDeleted":false,"meteringPoints":[{"id":"9ef69a17-ccb3-b090-9b75-5a30868c7425","meteringPointCode":"AT0080000816000000202204040112246","energyDirection":"generation","description":null},{"id":"a447d97e-c1a4-d94e-24e9-c17a66f9a006","meteringPointCode":"AT0082000816000000000000000889401","energyDirection":"consumption","description":null}],"energyCommunities":[{"id":"dc200a26-2dba-21c5-6bba-304b2a03df75","name":"EEG TW","source":null,"sourceId":"RC101900","meteringPoints":[{"id":"9ef69a17-ccb3-b090-9b75-5a30868c7425","meteringPointCode":"AT0080000816000000202204040112246","energyDirection":"generation","description":null,"participationPercentage":100.0},{"id":"a447d97e-c1a4-d94e-24e9-c17a66f9a006","meteringPointCode":"AT0082000816000000000000000889401","energyDirection":"consumption","description":null,"participationPercentage":100.0}]}]}}""";

    [Fact]
    public void OriginalRabbitMqJsonBuildsMetadataDashboardsWithoutLiveServices()
    {
        var message = EventPolicy.Parse(Encoding.UTF8.GetBytes(OriginalCustomerEventJson));
        Assert.Equal(Guid.Parse("55e7fc4a-29fe-0ec5-3a0a-821deb2211a3"), message.EventId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-09T00:00:00+00:00"), message.OccurredAtUtc);
        Assert.Equal("232", message.Customer.CustomerNumber);
        Assert.Equal(new[] { "generation", "consumption" },
            message.Customer.MeteringPoints!.Select(p => p.EnergyDirection));
        Assert.Equal("RC101900", message.Customer.EnergyCommunities![0].SourceId);
        Assert.Equal(message.Customer.MeteringPoints!.Select(p => p.MeteringPointCode),
            message.Customer.EnergyCommunities[0].MeteringPoints.Select(p => p.MeteringPointCode));

        var dashboards = GrafanaProvisioner.BuildDashboards(message.Customer);
        Assert.Equal(3, dashboards.Count);
        Assert.Contains(dashboards, dashboard => dashboard["title"]!.GetValue<string>()
            .StartsWith("Zählpunkt · Bezug", StringComparison.Ordinal));
        Assert.Contains(dashboards, dashboard => dashboard["title"]!.GetValue<string>()
            .StartsWith("Zählpunkt · Erzeugung", StringComparison.Ordinal));
        Assert.Contains(dashboards, dashboard => dashboard["title"]!.GetValue<string>()
            .StartsWith("Energiegemeinschaft · Übersicht", StringComparison.Ordinal));
        Assert.DoesNotContain("rawSql", JsonSerializer.Serialize(dashboards));
    }

    [Theory]
    [InlineData("customer.created")]
    [InlineData("customer.updated")]
    [InlineData("customer.deleted")]
    public void ContractRoundTrips(string eventType)
    {
        var message = Message() with { EventType = eventType };
        message = message with { Customer = message.Customer with { IsDeleted = eventType == "customer.deleted" } };
        Assert.Equal(message.EventId, EventPolicy.Parse(JsonSerializer.SerializeToUtf8Bytes(message, EventPolicy.Json)).EventId);
    }

    [Fact]
    public void RejectsUnsupportedAndMalformedEvents()
    {
        Assert.Throws<InvalidEventException>(() => EventPolicy.Parse(Encoding.UTF8.GetBytes("{}")));
        Assert.Throws<InvalidEventException>(() => EventPolicy.Parse(Encoding.UTF8.GetBytes("not json")));
        Assert.Throws<InvalidEventException>(() => EventPolicy.Validate(Message() with { SchemaVersion = 2 }));
        var message = Message();
        Assert.Throws<InvalidEventException>(() => EventPolicy.Validate(message with
        { Customer = message.Customer with { RegistrationCode = "K-100" } }));
    }

    [Fact]
    public void RabbitMqPayloadIgnoresCentralApiRegistrationCode()
    {
        var json = OriginalCustomerEventJson.Replace("\"email\":null",
            "\"email\":null,\"registrationCode\":\"K-100\"", StringComparison.Ordinal);
        var parsed = EventPolicy.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Null(parsed.Customer.RegistrationCode);
        Assert.Equal("232", parsed.Customer.CustomerNumber);
        Assert.Equal("RC101900", parsed.Customer.EnergyCommunities![0].SourceId);
        Assert.DoesNotContain("K-100", JsonSerializer.Serialize(parsed, EventPolicy.Json));
    }

    [Fact]
    public void MissingCollectionsPreserveStateWhileEmptyCollectionsRemoveIt()
    {
        var previous = Message().Customer;
        var incoming = previous with { MeteringPoints = null, EnergyCommunities = null };
        Assert.Equal(previous.MeteringPoints, EventPolicy.Merge(incoming, previous).MeteringPoints);
        Assert.Empty(EventPolicy.Merge(incoming with { MeteringPoints = [] }, previous).MeteringPoints!);
        Assert.Null(EventPolicy.Merge(previous, null).RegistrationCode);
    }

    [Fact]
    public void DeletionRemovesCollectionsEvenForHistoricalMessages()
    {
        var previous = Message().Customer;
        var merged = EventPolicy.Merge(previous with { IsDeleted = true, MeteringPoints = null }, previous);
        Assert.Empty(merged.MeteringPoints!);
        Assert.Empty(merged.EnergyCommunities!);
    }

    [Fact]
    public void DeletionDoesNotRequireAUsableRegistrationCode()
    {
        var message = Message();
        EventPolicy.Validate(message with
        {
            EventType = "customer.deleted",
            Customer = message.Customer with { IsDeleted = true, RegistrationCode = "" }
        });
    }

    [Fact]
    public void DashboardIdentityIsStableAndOrganizationScoped()
    {
        var customer = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var uid = ResourceIdentity.Dashboard(customer, "meter", resource);
        Assert.Equal(uid, ResourceIdentity.Dashboard(customer, "meter", resource));
        Assert.NotEqual(uid, ResourceIdentity.Dashboard(Guid.NewGuid(), "meter", resource));
        Assert.InRange(uid.Length, 1, 40);
    }

    [Fact]
    public void OrganizationDisplayNameChangesWithoutChangingClaimIdentity()
    {
        var customer = Message().Customer;
        var renamed = customer with { Name = "Updated" };
        Assert.Equal($"{customer.CustomerNumber} - {customer.Name}", ResourceIdentity.OrganizationName(customer));
        Assert.Equal($"{customer.CustomerNumber} - Updated", ResourceIdentity.OrganizationName(renamed));
        Assert.Equal(ResourceIdentity.Organization(customer.Id), ResourceIdentity.Organization(renamed.Id));
    }

    [Fact]
    public void DashboardsContainNoDataSourceOrQueriesOrCode()
    {
        var customer = Message().Customer;
        var dashboards = GrafanaProvisioner.BuildDashboards(customer);
        Assert.Equal(2, dashboards.Count);
        Assert.All(dashboards, dashboard => Assert.DoesNotContain("Customer overview", dashboard.ToJsonString()));
        Assert.StartsWith("Energiegemeinschaft · Übersicht",
            dashboards.Single(d => d["title"]!.GetValue<string>().StartsWith("Energiegemeinschaft"))
                ["title"]!.GetValue<string>());
        var json = JsonSerializer.Serialize(dashboards);
        Assert.DoesNotContain("datasource", json);
        Assert.DoesNotContain("rawSql", json);
        Assert.DoesNotContain(customer.RegistrationCode!, json);
        Assert.All(dashboards, dashboard => Assert.Contains(
            ResourceIdentity.OwnerTag(customer.Id), dashboard["tags"]!.ToJsonString()));
    }

    [Fact]
    public void TicketsAreSignedExpireAndNeverContainRawCode()
    {
        var clock = new TestTime();
        var secrets = Secrets(clock);
        var code = new string('a', 48);
        var hash = secrets.Hash(code);
        var id = Guid.NewGuid();
        var ticket = secrets.CreateTicket(id, hash);
        Assert.Equal(id, secrets.ReadTicket(ticket).CustomerId);
        Assert.DoesNotContain(code, Encoding.UTF8.GetString(Convert.FromBase64String(ticket.Split('.')[0])));
        Assert.Throws<RegistrationDeniedException>(() => secrets.ReadTicket("X" + ticket));
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Throws<RegistrationDeniedException>(() => secrets.ReadTicket(ticket));
        Assert.NotEqual(hash, secrets.Hash(code + "b"));
        Assert.False(secrets.Authenticate("invalid"));
    }

    internal static RegistrationSecrets Secrets(TimeProvider? time = null) =>
        new(Options.Create(new RegistrationOptions
        {
            HashKey = "unit-tests-only-hash-key-0123456789012345",
            InternalToken = "unit-tests-only-internal-token-0123456789"
        }), time ?? TimeProvider.System);

    internal static CustomerEvent Message()
    {
        var meter = Guid.NewGuid();
        return new(Guid.NewGuid(), "customer.created", 1, DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), "K-100", "Sample", "customer@example.invalid", false, new string('a', 48),
                [new(meter, "AT001000000000000000000000000001", "consumption", "House")],
                [new(Guid.NewGuid(), "Community", "EDA", "external-123",
                    [new(meter, "AT001000000000000000000000000001", "consumption", "House", 75)])]));
    }

    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
