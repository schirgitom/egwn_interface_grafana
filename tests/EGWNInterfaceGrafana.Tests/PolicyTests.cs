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
    public void DashboardsContainNoDataSourceOrQueriesOrCode()
    {
        var customer = Message().Customer;
        var dashboards = GrafanaProvisioner.BuildDashboards(customer);
        Assert.Equal(3, dashboards.Count);
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
