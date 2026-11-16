using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using EGWNInterfaceGrafana.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using Xunit;

namespace EGWNInterfaceGrafana.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EGWN_LIVE_INTEGRATION") != "true")
            Skip = "Explicitly enable EGWN_LIVE_INTEGRATION and supply the live test credentials.";
    }
}

public sealed class LiveEnvironmentTests
{
    [LiveFact]
    public async Task ProvisionsRealMeasurementsThroughRabbitMqAndCleansUp()
    {
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value : throw new InvalidOperationException($"{name} is required.");

        var persistence = new NpgsqlConnectionStringBuilder(Required("EGWN_LIVE_PROVISIONING_CONNECTION"));
        var databaseHost = persistence.Host ?? throw new InvalidOperationException("Persistence host is required.");
        if (databaseHost != "egwn-timescaledb" || persistence.Database != "egwndata2")
            throw new InvalidOperationException("Live tests may only access egwndata2 over the private egwn-timescaledb network address.");
        var grafanaUser = Required("EGWN_LIVE_GRAFANA_USER");
        var grafanaPassword = Required("EGWN_LIVE_GRAFANA_PASSWORD");
        var rabbitUser = Required("EGWN_LIVE_RABBIT_USER");
        var rabbitPassword = Required("EGWN_LIVE_RABBIT_PASSWORD");
        var grafanaHost = Required("EGWN_LIVE_GRAFANA_HOST");
        var rabbitHost = Required("EGWN_LIVE_RABBIT_HOST");
        var token = Guid.NewGuid().ToString("N");
        var customerNumber = "Live" + token[..12];
        var credentialKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var service = new NpgsqlConnectionStringBuilder(persistence.ConnectionString)
        {
            Database = "egwn_live_test_" + token
        };
        var measurement = new TenantMeasurementProvisioner(Options.Create(new MeasurementOptions
        {
            Enabled = true, ProvisioningConnectionString = persistence.ConnectionString,
            GrafanaDatabaseHost = databaseHost,
            GrafanaDatabasePort = persistence.Port,
            CredentialKey = credentialKey
        }));

        string meterCode;
        string communityId;
        string direction;
        DateTimeOffset lastReading;
        await using (var connection = new NpgsqlConnection(persistence.ConnectionString))
        {
            await connection.OpenAsync();
            await using var find = new NpgsqlCommand("""
                SELECT mp.meter_point_number, mc.community_id, mp.energy_direction, MAX(r.timestamp)
                FROM public.meter_points mp
                JOIN public.meter_point_communities mc ON mc.meter_id = mp."Id"
                JOIN public.raw_measurements r ON r.meter_id = mp."Id" AND r.community_id = mc.community_id
                WHERE mp.energy_direction IN ('consumption', 'generation')
                GROUP BY mp.meter_point_number, mc.community_id, mp.energy_direction
                ORDER BY MAX(r.timestamp) DESC LIMIT 1
                """, connection);
            await using var row = await find.ExecuteReaderAsync();
            if (!await row.ReadAsync())
                throw new InvalidOperationException("No measurement with a supported direction is available for the live test.");
            meterCode = row.GetString(0);
            communityId = row.GetString(1);
            direction = row.GetString(2);
            lastReading = row.GetFieldValue<DateTimeOffset>(3);
        }

        var example = PolicyTests.Message();
        var meter = example.Customer.MeteringPoints![0] with
        {
            MeteringPointCode = meterCode, EnergyDirection = direction
        };
        var community = example.Customer.EnergyCommunities![0] with
        {
            SourceId = communityId,
            MeteringPoints = [example.Customer.EnergyCommunities[0].MeteringPoints[0] with
            { MeteringPointCode = meterCode, EnergyDirection = direction }]
        };
        var message = example with
        {
            Customer = example.Customer with
            {
                CustomerNumber = customerNumber, Name = "EGWN live integration test",
                Email = "live-test@example.invalid",
                RegistrationCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                MeteringPoints = [meter], EnergyCommunities = [community]
            }
        };
        await using var db = new ServiceDbContext(new DbContextOptionsBuilder<ServiceDbContext>()
            .UseNpgsql(service.ConnectionString).Options);
        using var grafana = new HttpClient { BaseAddress = new Uri($"http://{grafanaHost}:3000/") };
        grafana.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{grafanaUser}:{grafanaPassword}")));
        var grafanaOptions = Options.Create(new GrafanaOptions
        { AdminUser = grafanaUser, AdminPassword = grafanaPassword });
        var queue = "egwn.live." + token;
        var exchange = queue;
        var rabbit = new RabbitOptions
        {
            Enabled = true, HostName = rabbitHost, UserName = rabbitUser, Password = rabbitPassword,
            Exchange = exchange, Queue = queue, RoutingKey = queue, RetrySeconds = 1, MaxAttempts = 2
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ServiceDbContext>(o => o.UseNpgsql(service.ConnectionString));
        services.AddSingleton(PolicyTests.Secrets());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(measurement);
        services.AddScoped<EventProcessor>();
        services.AddScoped<IGrafanaProvisioner>(sp => new GrafanaProvisioner(grafana, grafanaOptions,
            sp.GetRequiredService<ServiceDbContext>(), measurement));
        services.AddSingleton<IIdentityRevoker>(new TestRevoker());
        await using var provider = services.BuildServiceProvider();
        var status = new RabbitStatus();
        using var consumer = new RabbitConsumer(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(rabbit), status, NullLogger<RabbitConsumer>.Instance);
        var factory = new ConnectionFactory
        { HostName = rabbitHost, UserName = rabbitUser, Password = rabbitPassword };
        JsonObject? original = null;
        try
        {
            await db.Database.MigrateAsync();
            using var originalResponse = await grafana.GetAsync("api/v1/sso-settings/generic_oauth");
            originalResponse.EnsureSuccessStatusCode();
            original = (await originalResponse.Content.ReadFromJsonAsync<JsonObject>())!["settings"]!.AsObject();
            await using var broker = await factory.CreateConnectionAsync();
            await using var channel = await broker.CreateChannelAsync();
            try
            {
                await consumer.StartAsync(default);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                while (!status.Connected) await Task.Delay(200, timeout.Token);
                await channel.BasicPublishAsync(exchange, queue,
                    JsonSerializer.SerializeToUtf8Bytes(message, EventPolicy.Json));
                while (!await db.Events.AsNoTracking().AnyAsync(e => e.Id == message.EventId && e.Completed, timeout.Token))
                {
                    if (await channel.MessageCountAsync(queue + ".dead", timeout.Token) > 0)
                        throw new InvalidOperationException("Live customer event was dead-lettered.");
                    await Task.Delay(200, timeout.Token);
                }
                var state = await db.Customers.AsNoTracking().SingleAsync();
                Assert.True(state.Ready);
                var tenant = ResourceIdentity.TenantSchema(customerNumber);
                var reader = new NpgsqlConnectionStringBuilder(persistence.ConnectionString)
                {
                    Username = tenant.Replace("grafana_customer_", "grafana_reader_", StringComparison.Ordinal),
                    Password = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
                        Encoding.UTF8.GetBytes(credentialKey), Encoding.UTF8.GetBytes("postgres:" + customerNumber)))
                };
                await using (var tenantConnection = new NpgsqlConnection(reader.ConnectionString))
                {
                    await tenantConnection.OpenAsync();
                    await using var count = new NpgsqlCommand($"SELECT count(*) FROM {tenant}.raw_measurements", tenantConnection);
                    Assert.True((long)(await count.ExecuteScalarAsync())! > 0);
                    await using var denied = new NpgsqlCommand("SELECT count(*) FROM public.raw_measurements", tenantConnection);
                    await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteScalarAsync());
                }
                var uid = ResourceIdentity.Dashboard(message.Customer.Id, "datasource", message.Customer.Id);
                using var getSource = new HttpRequestMessage(HttpMethod.Get, $"api/datasources/uid/{uid}");
                getSource.Headers.Add("X-Grafana-Org-Id", state.OrganizationId!.Value.ToString());
                using var sourceResponse = await grafana.SendAsync(getSource);
                sourceResponse.EnsureSuccessStatusCode();
                var dashboards = GrafanaProvisioner.BuildDashboards(message.Customer,
                    new TenantDataSource(tenant, reader.Username, reader.Password, databaseHost,
                        persistence.Port, persistence.Database!));
                var targets = dashboards.SelectMany(d => d["panels"]!.AsArray())
                    .Where(p => p!["type"]!.GetValue<string>() != "text")
                    .Select((p, index) => new
                    {
                        refId = $"Q{index}", datasource = new { uid },
                        rawSql = p!["targets"]![0]!["rawSql"]!.GetValue<string>(),
                        format = p["targets"]![0]!["format"]!.GetValue<string>(),
                        rawQuery = true, editorMode = "code", intervalMs = 3600000, maxDataPoints = 100
                    }).ToArray();
                Assert.Equal(9, targets.Length);
                using var query = new HttpRequestMessage(HttpMethod.Post, "api/ds/query");
                query.Headers.Add("X-Grafana-Org-Id", state.OrganizationId.Value.ToString());
                query.Content = JsonContent.Create(new
                {
                    from = lastReading.AddHours(-1).ToUnixTimeMilliseconds().ToString(),
                    to = lastReading.AddHours(1).ToUnixTimeMilliseconds().ToString(),
                    queries = targets
                });
                using var resultResponse = await grafana.SendAsync(query);
                resultResponse.EnsureSuccessStatusCode();
                var result = await resultResponse.Content.ReadFromJsonAsync<JsonElement>();
                foreach (var target in targets)
                {
                    var entry = result.GetProperty("results").GetProperty(target.refId);
                    Assert.False(entry.TryGetProperty("error", out var error), error.ToString());
                }
                var meterSeries = result.GetProperty("results").GetProperty("Q4");
                Assert.Contains(meterSeries.GetProperty("frames").EnumerateArray(),
                    frame => frame.GetProperty("data").GetProperty("values")[0].GetArrayLength() > 0);
            }
            finally
            {
                await consumer.StopAsync(default);
                await channel.QueueDeleteAsync(queue);
                await channel.QueueDeleteAsync(queue + ".dead");
                await channel.ExchangeDeleteAsync(exchange);
            }
        }
        finally
        {
            try
            {
                try
                {
                    if (await db.Database.CanConnectAsync() &&
                        await db.Customers.AsNoTracking().AnyAsync(c => c.Id == message.Customer.Id))
                    {
                        await using var transaction = await db.LockCustomerAsync(message.Customer.Id, default);
                        await new GrafanaProvisioner(grafana, grafanaOptions, db, measurement)
                            .DeleteAsync(message.Customer.Id, null, default);
                        await transaction.CommitAsync();
                    }
                    else
                        await measurement.DeleteAsync(customerNumber, message.Customer.Id, default);
                }
                finally
                {
                    if (original is not null)
                        await RestoreMappingAsync(grafana, original);
                }
            }
            finally
            {
                if (await db.Database.CanConnectAsync()) await db.Database.EnsureDeletedAsync();
            }
        }
    }

    private static async Task RestoreMappingAsync(HttpClient grafana, JsonObject original)
    {
        using var response = await grafana.PatchAsJsonAsync("api/v1/sso-settings/generic_oauth", new
        {
            settings = new
            {
                org_mapping = original["orgMapping"]?.GetValue<string>() ?? "",
                org_attribute_path = original["orgAttributePath"]?.GetValue<string>(),
                role_attribute_path = original["roleAttributePath"]?.GetValue<string>(),
                role_attribute_strict = original["roleAttributeStrict"]?.GetValue<bool>(),
                skip_org_role_sync = original["skipOrgRoleSync"]?.GetValue<bool>(),
                allow_assign_grafana_admin = original["allowAssignGrafanaAdmin"]?.GetValue<bool>()
            }
        });
        response.EnsureSuccessStatusCode();
    }

    private sealed class TestRevoker : IIdentityRevoker
    {
        public Task RevokeAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("Live test must not revoke existing Keycloak users.");
    }
}
