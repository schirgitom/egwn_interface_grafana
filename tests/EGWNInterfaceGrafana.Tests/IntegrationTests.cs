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
    public async Task CustomerDataSourceExposesOwnMetersAndOnlyCommunityAggregates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initial = PolicyTests.Message();
        var original = initial with { Customer = initial.Customer with
        { CustomerNumber = "K" + Guid.NewGuid().ToString("N")[..12] } };
        var customer = original.Customer;
        var ownCode = customer.MeteringPoints![0].MeteringPointCode;
        var community = customer.EnergyCommunities![0].SourceId!;
        var otherCode = "AT999999999999999999999999999999";
        await fixture.Db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE public.meter_points ("Id" bigint PRIMARY KEY, meter_point_number text, energy_direction text);
            CREATE TABLE public.meter_point_communities (meter_id bigint, community_id text);
            CREATE TABLE public.raw_measurements (meter_id bigint, community_id text, timestamp timestamptz,
                total_consumption numeric, grid_share numeric, community_share numeric);
            CREATE TABLE public.kpi_measurements (community_id text, timestamp timestamptz, autarky numeric,
                own_consumption numeric, community numeric, feed numeric, remaining_demand numeric);
            """);
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.meter_points VALUES (1, {ownCode}, 'consumption'),
                (2, {otherCode}, 'generation'), (3, 'AT000003', 'generation'),
                (4, 'AT000004', 'generation');
            INSERT INTO public.meter_point_communities VALUES (1, {community}), (2, {community}),
                (3, {community}), (4, {community}), (2, 'foreign');
            INSERT INTO public.raw_measurements VALUES
              (1, {community}, NOW(), 10, 4, 6), (2, {community}, NOW(), 20, 8, 12),
              (3, {community}, NOW(), 30, 12, 18), (4, {community}, NOW(), 40, 16, 24),
              (2, 'foreign', NOW(), 99, 99, 0);
            INSERT INTO public.kpi_measurements VALUES ({community}, NOW(), 0, 0, 0, 0, 0);
            """);
        var source = new TenantMeasurementProvisioner(Options.Create(new MeasurementOptions
        {
            Enabled = true, ProvisioningConnectionString = fixture.ConnectionString,
            GrafanaDatabaseHost = "postgres", CredentialKey = new string('s', 48)
        }));
        var provisioner = new GrafanaProvisioner(fixture.Grafana, Options.Create(new GrafanaOptions
        { AdminUser = "integration", AdminPassword = "integration-tests-only" }), fixture.Db, source);
        try
        {
            await new EventProcessor(fixture.Db, provisioner, fixture.Identities,
                fixture.Secrets, TimeProvider.System, NullLogger<EventProcessor>.Instance)
                .ProcessAsync(original, default);
            var orgId = (await fixture.Db.Customers.AsNoTracking().SingleAsync()).OrganizationId!.Value;
            var tenant = await source.ReconcileAsync(customer, default);
            await source.EnableAsync(customer.CustomerNumber, customer.Id, default);
            var reader = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { Username = tenant.User, Password = tenant.Password, Pooling = false };
            await using var connection = new NpgsqlConnection(reader.ConnectionString);
            await connection.OpenAsync();
            async Task<long> CountAsync(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return (long)(await command.ExecuteScalarAsync())!;
            }
            Assert.Equal(1, await CountAsync($"SELECT count(*) FROM {tenant.Schema}.raw_measurements"));
            Assert.Equal(1, await CountAsync($"SELECT count(*) FROM {tenant.Schema}.meter_points"));
            Assert.Equal(1, await CountAsync($"SELECT count(*) FROM {tenant.Schema}.meter_point_communities"));
            Assert.Equal(1, await CountAsync($"SELECT count(*) FROM {tenant.Schema}.kpi_measurements"));
            Assert.Equal(1, await CountAsync($"SELECT count(*) FROM {tenant.Schema}.community_totals"));
            Assert.Equal(90, await CountAsync($"SELECT total::bigint FROM {tenant.Schema}.community_totals"));
            Assert.Equal(0, await CountAsync(
                $"SELECT count(*) FROM {tenant.Schema}.community_totals WHERE energy_direction='consumption'"));
            await Assert.ThrowsAsync<PostgresException>(() => CountAsync("SELECT count(*) FROM public.raw_measurements"));
            var dashboards = GrafanaProvisioner.BuildDashboards(customer, tenant);
            var generationMeter = customer.MeteringPoints[0] with { EnergyDirection = "generation" };
            var generationCommunity = customer.EnergyCommunities[0] with
            {
                MeteringPoints = [customer.EnergyCommunities[0].MeteringPoints[0] with
                { EnergyDirection = "generation" }]
            };
            var generationDashboards = GrafanaProvisioner.BuildDashboards(customer with
            {
                MeteringPoints = [generationMeter], EnergyCommunities = [generationCommunity]
            }, tenant);
            Assert.Contains("Erzeugung", generationDashboards.Single(d =>
                d["title"]!.GetValue<string>().StartsWith("Meter -"))["panels"]!.ToJsonString());
            Assert.Contains("rawSql", dashboards.Single(d => d["title"]!.GetValue<string>().StartsWith("Meter -")).ToJsonString());
            Assert.Equal(3, dashboards.Count);
            Assert.DoesNotContain(tenant.Password, JsonSerializer.Serialize(dashboards));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"api/datasources/uid/{ResourceIdentity.Dashboard(customer.Id, "datasource", customer.Id)}");
            request.Headers.Add("X-Grafana-Org-Id", orgId.ToString());
            using var response = await fixture.Grafana.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var dataSource = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(orgId, dataSource.GetProperty("orgId").GetInt64());
            var targets = dashboards.SelectMany(d => d["panels"]!.AsArray())
                .Where(p => p!["type"]!.GetValue<string>() != "text")
                .Select((p, index) => new
                {
                    refId = $"Q{index}", datasource = new { uid = dataSource.GetProperty("uid").GetString() },
                    rawSql = p!["targets"]![0]!["rawSql"]!.GetValue<string>(),
                    format = p["targets"]![0]!["format"]!.GetValue<string>(), rawQuery = true,
                    editorMode = "code", intervalMs = 3600000, maxDataPoints = 100
                }).ToArray();
            Assert.Equal(9, targets.Length);
            using var query = new HttpRequestMessage(HttpMethod.Post, "api/ds/query");
            query.Headers.Add("X-Grafana-Org-Id", orgId.ToString());
            query.Content = JsonContent.Create(new
            {
                from = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds().ToString(),
                to = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds().ToString(),
                queries = targets
            });
            using var queryResult = await fixture.Grafana.SendAsync(query);
            queryResult.EnsureSuccessStatusCode();
            var result = await queryResult.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var target in targets)
            {
                var entry = result.GetProperty("results").GetProperty(target.refId);
                Assert.False(entry.TryGetProperty("error", out var error), error.ToString());
                Assert.NotEmpty(entry.GetProperty("frames").EnumerateArray());
            }
            await Assert.ThrowsAsync<InvalidEventException>(() => source.ReconcileAsync(customer with
            {
                EnergyCommunities = [customer.EnergyCommunities[0] with { SourceId = "" }]
            }, default));
            await Assert.ThrowsAsync<PostgresException>(() => CountAsync($"SELECT count(*) FROM {tenant.Schema}.raw_measurements"));
            await connection.CloseAsync();
            await source.ReconcileAsync(customer with
            {
                MeteringPoints = [],
                EnergyCommunities = [customer.EnergyCommunities[0] with { MeteringPoints = [] }]
            }, default);
            await using (var blocked = new NpgsqlConnection(reader.ConnectionString))
                await Assert.ThrowsAsync<PostgresException>(() => blocked.OpenAsync());
            await source.EnableAsync(customer.CustomerNumber, customer.Id, default);
            await connection.OpenAsync();
            Assert.Equal(0, await CountAsync($"SELECT count(*) FROM {tenant.Schema}.raw_measurements"));
            await source.DeleteAsync(customer.CustomerNumber, customer.Id, default);
            await Assert.ThrowsAsync<PostgresException>(() => CountAsync($"SELECT count(*) FROM {tenant.Schema}.raw_measurements"));
            await connection.CloseAsync();
            await using (var blocked = new NpgsqlConnection(reader.ConnectionString))
                await Assert.ThrowsAsync<PostgresException>(() => blocked.OpenAsync());
        }
        finally
        {
            await provisioner.DeleteAsync(customer.Id, null, default);
        }
    }

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
