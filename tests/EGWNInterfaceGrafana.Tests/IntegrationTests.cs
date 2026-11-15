using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using EGWNInterfaceGrafana.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using Xunit;

namespace EGWNInterfaceGrafana.Tests;

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EGWN_INTEGRATION") != "true")
            Skip = "Set EGWN_INTEGRATION=true and start compose.integration.yml.";
    }
}

public sealed class IntegrationTests
{
    [IntegrationFact]
    public async Task ReconcilesRealGrafanaAndPostgresWithRegistrationLifecycle()
    {
        await using var fixture = await Fixture.CreateAsync();
        var message = PolicyTests.Message();
        var id = message.Customer.Id;
        try
        {
            await fixture.ProcessAsync(message);
            await fixture.ProcessAsync(message);
            Assert.Equal(1, await fixture.Db.Events.CountAsync());
            Assert.Equal(3, (await fixture.DashboardsAsync(id)).Count);
            var stored = await fixture.Db.Customers.AsNoTracking().SingleAsync();
            Assert.True(stored.Ready);
            Assert.DoesNotContain(message.Customer.RegistrationCode!, stored.SnapshotJson);
            var settings = await fixture.Grafana.GetStringAsync("api/v1/sso-settings/generic_oauth");
            using var settingsDocument = JsonDocument.Parse(settings);
            var orgMapping = settingsDocument.RootElement.GetProperty("settings").GetProperty("orgMapping").GetString()!;
            Assert.Contains($"{ResourceIdentity.Organization(id)}:{stored.OrganizationId}:Viewer", orgMapping);
            Assert.DoesNotContain("*:", orgMapping);
            var registration = fixture.Registration;
            var resolution = await registration.ResolveAsync(message.Customer.RegistrationCode!, default);
            Assert.Equal(id, (await registration.AssignAsync(resolution.Ticket, "user-one", default)).CustomerId);
            Assert.Equal(id, (await registration.AssignAsync(resolution.Ticket, "user-two", default)).CustomerId);

            var next = message with { EventId = Guid.NewGuid(), EventType = "customer.updated", OccurredAtUtc = message.OccurredAtUtc.AddSeconds(1),
                Customer = message.Customer with { Name = "Renamed", RegistrationCode = new string('b', 48), MeteringPoints = [], EnergyCommunities = [] } };
            await fixture.ProcessAsync(next);
            Assert.Single(await fixture.DashboardsAsync(id));
            await Assert.ThrowsAsync<RegistrationDeniedException>(() => fixture.Registration.ResolveAsync(message.Customer.RegistrationCode!, default));
            await Assert.ThrowsAsync<RegistrationDeniedException>(() => fixture.Registration.AssignAsync(resolution.Ticket, "user-three", default));
            Assert.Equal(id, (await fixture.Registration.MembershipAsync("user-one", default)).CustomerId);
            await fixture.ProcessAsync(message with { EventId = Guid.NewGuid(), OccurredAtUtc = message.OccurredAtUtc.AddSeconds(-1) });
            Assert.Single(await fixture.DashboardsAsync(id));
            await Assert.ThrowsAsync<InvalidEventException>(() => fixture.ProcessAsync(next with { EventId = Guid.NewGuid() }));

            var deleted = next with { EventId = Guid.NewGuid(), EventType = "customer.deleted", OccurredAtUtc = next.OccurredAtUtc.AddSeconds(1),
                Customer = next.Customer with { IsDeleted = true } };
            await fixture.ProcessAsync(deleted);
            await fixture.ProcessAsync(deleted);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.Grafana.GetAsync($"api/orgs/name/{ResourceIdentity.Organization(id)}")).StatusCode);
            await Assert.ThrowsAsync<RegistrationDeniedException>(() => fixture.Registration.MembershipAsync("user-one", default));
            Assert.Equal(2, fixture.Identities.Revoked.Count);
            Assert.True((await fixture.Db.Customers.AsNoTracking().SingleAsync()).IsDeleted);
        }
        finally { await fixture.DeleteOrganizationAsync(id); }
    }

    [IntegrationFact]
    public async Task FailedProvisioningRemainsClosedAndCanResumeAfterCrash()
    {
        await using var fixture = await Fixture.CreateAsync();
        var message = PolicyTests.Message();
        var failing = new FailingProvisioner();
        await Assert.ThrowsAsync<HttpRequestException>(() => new EventProcessor(fixture.Db, failing, fixture.Identities,
            fixture.Secrets, TimeProvider.System, NullLogger<EventProcessor>.Instance).ProcessAsync(message, default));
        fixture.Db.ChangeTracker.Clear();
        var pending = await fixture.Db.Customers.AsNoTracking().SingleAsync();
        Assert.False(pending.Ready);
        Assert.False((await fixture.Db.Events.AsNoTracking().SingleAsync()).Completed);
        await Assert.ThrowsAsync<RegistrationDeniedException>(() => fixture.Registration.ResolveAsync(message.Customer.RegistrationCode!, default));
        try
        {
            await fixture.ProcessAsync(message);
            Assert.True((await fixture.Db.Customers.AsNoTracking().SingleAsync()).Ready);
            Assert.True((await fixture.Db.Events.AsNoTracking().SingleAsync()).Completed);
        }
        finally { await fixture.DeleteOrganizationAsync(message.Customer.Id); }
    }

    [IntegrationFact]
    public async Task ConcurrentDuplicateDeliveryAndInterruptedDeletionRemainSafe()
    {
        await using var fixture = await Fixture.CreateAsync();
        var message = PolicyTests.Message();
        var id = message.Customer.Id;
        try
        {
            async Task DeliverAsync()
            {
                await using var db = new ServiceDbContext(new DbContextOptionsBuilder<ServiceDbContext>()
                    .UseNpgsql(fixture.ConnectionString).Options);
                var provisioner = new GrafanaProvisioner(fixture.Grafana, Options.Create(new GrafanaOptions
                    { AdminUser = "integration", AdminPassword = "integration-tests-only" }), db);
                await new EventProcessor(db, provisioner, fixture.Identities, fixture.Secrets,
                    TimeProvider.System, NullLogger<EventProcessor>.Instance).ProcessAsync(message, default);
            }
            await Task.WhenAll(DeliverAsync(), DeliverAsync());
            Assert.Equal(1, await fixture.Db.Events.CountAsync());
            Assert.Equal(3, (await fixture.DashboardsAsync(id)).Count);
            var resolution = await fixture.Registration.ResolveAsync(message.Customer.RegistrationCode!, default);
            await fixture.Registration.AssignAsync(resolution.Ticket, "delete-test-user", default);
            var conflicting = message with { EventId = Guid.NewGuid(), Customer = message.Customer with { Id = Guid.NewGuid() } };
            await Assert.ThrowsAsync<InvalidEventException>(() => fixture.ProcessAsync(conflicting));
            fixture.Db.ChangeTracker.Clear();
            var deletion = message with { EventId = Guid.NewGuid(), EventType = "customer.deleted",
                OccurredAtUtc = message.OccurredAtUtc.AddSeconds(1), Customer = message.Customer with { IsDeleted = true } };
            await Assert.ThrowsAsync<HttpRequestException>(() => new EventProcessor(fixture.Db,
                new FailingProvisioner(), fixture.Identities, fixture.Secrets, TimeProvider.System,
                NullLogger<EventProcessor>.Instance).ProcessAsync(deletion, default));
            fixture.Db.ChangeTracker.Clear();
            await Assert.ThrowsAsync<RegistrationDeniedException>(() => fixture.Registration.MembershipAsync("delete-test-user", default));
            await Assert.ThrowsAsync<RegistrationDeniedException>(() => fixture.Registration.AssignAsync(resolution.Ticket, "late-user", default));
            await fixture.ProcessAsync(deletion);
            Assert.True((await fixture.Db.Events.SingleAsync(e => e.Id == deletion.EventId)).Completed);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.Grafana.GetAsync(
                $"api/orgs/name/{ResourceIdentity.Organization(id)}")).StatusCode);
        }
        finally { await fixture.DeleteOrganizationAsync(id); }
    }

    [IntegrationFact]
    public async Task RealRabbitConsumerAcknowledgesDuplicatesAndDeadLettersInvalidMessages()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queue = "egwn.integration." + Guid.NewGuid().ToString("N");
        var settings = new RabbitOptions { Enabled = true, HostName = "rabbitmq", UserName = "integration",
            Password = "integration-tests-only", Queue = queue, RoutingKey = queue, Exchange = "egwn.integration",
            RetrySeconds = 1, MaxAttempts = 2 };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ServiceDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddSingleton(fixture.Secrets);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<EventProcessor>();
        services.AddScoped<IGrafanaProvisioner>(sp => new GrafanaProvisioner(fixture.Grafana,
            Options.Create(new GrafanaOptions { AdminUser = "integration", AdminPassword = "integration-tests-only" }),
            sp.GetRequiredService<ServiceDbContext>()));
        services.AddSingleton<IIdentityRevoker>(fixture.Identities);
        await using var provider = services.BuildServiceProvider();
        var status = new RabbitStatus();
        using var consumer = new RabbitConsumer(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings), status, NullLogger<RabbitConsumer>.Instance);
        var factory = new ConnectionFactory { HostName = "rabbitmq", UserName = "integration", Password = "integration-tests-only" };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var message = PolicyTests.Message();
        try
        {
            await consumer.StartAsync(default);
            await WaitAsync(() => Task.FromResult(status.Connected));
            var body = JsonSerializer.SerializeToUtf8Bytes(message, EventPolicy.Json);
            await channel.BasicPublishAsync(settings.Exchange, settings.RoutingKey, body);
            await channel.BasicPublishAsync(settings.Exchange, settings.RoutingKey, body);
            await channel.BasicPublishAsync("", queue, Encoding.UTF8.GetBytes("{}"));
            await WaitAsync(async () => await fixture.Db.Events.AsNoTracking().CountAsync(e => e.Completed) == 1);
            await WaitAsync(async () => await channel.MessageCountAsync(queue + ".dead") == 1);
            await WaitAsync(async () => await channel.MessageCountAsync(queue) == 0);
            var dead = await channel.BasicGetAsync(queue + ".dead", autoAck: true);
            Assert.NotNull(dead);
            Assert.Equal("{}", Encoding.UTF8.GetString(dead.Body.ToArray()));
        }
        finally
        {
            await consumer.StopAsync(default);
            await channel.QueueDeleteAsync(queue);
            await channel.QueueDeleteAsync(queue + ".dead");
            await fixture.DeleteOrganizationAsync(message.Customer.Id);
        }
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!await condition()) await Task.Delay(200, timeout.Token);
    }

    private sealed class FailingProvisioner : IGrafanaProvisioner
    {
        public Task<long> ReconcileAsync(CustomerSnapshot c, long? id, CancellationToken ct) => throw new HttpRequestException("Simulated outage");
        public Task DeleteAsync(Guid id, long? org, CancellationToken ct) => throw new HttpRequestException("Simulated outage");
    }

    private sealed class FakeIdentities : IIdentityRevoker
    {
        public HashSet<string> Revoked { get; } = [];
        public Task RevokeAsync(string userId, CancellationToken ct) { Revoked.Add(userId); return Task.CompletedTask; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string ConnectionString { get; init; }
        public required ServiceDbContext Db { get; init; }
        public required HttpClient Grafana { get; init; }
        public RegistrationSecrets Secrets { get; } = PolicyTests.Secrets();
        public FakeIdentities Identities { get; } = new();
        public GrafanaProvisioner Provisioner => new(Grafana, Options.Create(new GrafanaOptions
        { AdminUser = "integration", AdminPassword = "integration-tests-only" }), Db);
        public RegistrationService Registration => new(Db, Secrets, NullLogger<RegistrationService>.Instance);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new NpgsqlConnectionStringBuilder
            {
                Host = "postgres", Username = "integration", Password = "integration-tests-only",
                Database = "egwn_test_" + Guid.NewGuid().ToString("N")
            }.ConnectionString;
            var db = new ServiceDbContext(new DbContextOptionsBuilder<ServiceDbContext>().UseNpgsql(connection).Options);
            await db.Database.MigrateAsync();
            var client = new HttpClient { BaseAddress = new Uri("http://grafana:3000/") };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("integration:integration-tests-only")));
            return new Fixture { ConnectionString = connection, Db = db, Grafana = client };
        }

        public async Task ProcessAsync(CustomerEvent message)
        {
            Db.ChangeTracker.Clear();
            await new EventProcessor(Db, Provisioner, Identities, Secrets, TimeProvider.System,
                NullLogger<EventProcessor>.Instance).ProcessAsync(message, default);
            Db.ChangeTracker.Clear();
        }

        public async Task<List<JsonElement>> DashboardsAsync(Guid id)
        {
            var state = await Db.Customers.AsNoTracking().SingleAsync(c => c.Id == id);
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/search?type=dash-db");
            request.Headers.Add("X-Grafana-Org-Id", state.OrganizationId.ToString());
            using var response = await Grafana.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<List<JsonElement>>())!;
        }

        public async Task DeleteOrganizationAsync(Guid id)
        {
            using var transaction = await Db.LockCustomerAsync(id, default);
            await Provisioner.DeleteAsync(id, null, default);
            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.Database.EnsureDeletedAsync();
            await Db.DisposeAsync();
            Grafana.Dispose();
        }
    }
}
