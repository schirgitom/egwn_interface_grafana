using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using EGWNInterfaceGrafana.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EGWNInterfaceGrafana.Tests;

public sealed class LivePersistenceFactAttribute : FactAttribute
{
    public LivePersistenceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EGWN_LIVE_PERSISTENCE") != "true")
            Skip = "Set EGWN_LIVE_PERSISTENCE=true and supply the live test configuration.";
    }
}

public sealed class LivePersistenceIntegrationTests(ITestOutputHelper output)
{
    [LivePersistenceFact]
    public async Task ProvisionRealPersistenceUserAndGrafanaDashboard()
    {
        var secrets = new ConfigurationBuilder().AddUserSecrets("egwn-interface-grafana-api").Build();
        string Required(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
            : secrets[name] is { Length: > 0 } stored ? stored
            : name == "EGWN_LIVE_PERSISTENCE_TOKEN" &&
              secrets["Measurement:ServiceToken"] is { Length: > 0 } serviceToken ? serviceToken
            : throw new InvalidOperationException($"{name} is required.");

        var dbConnection = new NpgsqlConnectionStringBuilder(Required("EGWN_LIVE_DB_CONNECTION"));
        if (dbConnection.Database != "egwndata2")
            throw new InvalidOperationException("The live test may only read egwndata2.");
        var persistenceUrl = Required("EGWN_LIVE_PERSISTENCE_URL");
        if (!Uri.TryCreate(persistenceUrl, UriKind.Absolute, out var persistenceUri) ||
            persistenceUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The Persistence API must use HTTPS.");
        var token = Required("EGWN_LIVE_PERSISTENCE_TOKEN");
        if (token.Length < 32)
            throw new InvalidOperationException("The Persistence service token is too short.");
        var grafanaUrl = Required("EGWN_LIVE_GRAFANA_URL");
        var grafanaUser = Required("EGWN_LIVE_GRAFANA_USER");
        var grafanaPassword = Required("EGWN_LIVE_GRAFANA_PASSWORD");
        var grafanaDbHost = Required("EGWN_LIVE_GRAFANA_DB_HOST");
        var keepSetting = Environment.GetEnvironmentVariable("EGWN_LIVE_KEEP_RESOURCES");
        if (keepSetting is not null and not ("true" or "false"))
            throw new InvalidOperationException("EGWN_LIVE_KEEP_RESOURCES must be true or false.");
        var keepResources = keepSetting == "true";

        var supplied = EventPolicy.Parse(Encoding.UTF8.GetBytes(PolicyTests.OriginalCustomerEventJson)).Customer;
        var customerId = Guid.NewGuid();
        var customer = supplied with
        {
            Id = customerId, CustomerNumber = "Live" + customerId.ToString("N")[..12],
            Name = "EGWN integration test", Email = null, RegistrationCode = null,
            EnergyCommunities = supplied.EnergyCommunities!.Select(c => c with
                { Id = Guid.NewGuid() }).ToArray()
        };
        var communityId = customer.EnergyCommunities![0].SourceId!;
        var readings = new Dictionary<Guid, DateTimeOffset>();
        await using var db = new ServiceDbContext(new DbContextOptionsBuilder<ServiceDbContext>()
            .UseNpgsql(dbConnection.ConnectionString).Options);
        await using (var source = new NpgsqlConnection(dbConnection.ConnectionString))
        {
            await source.OpenAsync();
            foreach (var point in customer.MeteringPoints!)
            {
                await using var find = new NpgsqlCommand("""
                    SELECT MAX(r.timestamp) FROM public.raw_measurements r
                    JOIN public.meter_points mp ON mp."Id" = r.meter_id
                    JOIN public.meter_point_communities mc ON mc.meter_id = mp."Id" AND mc.community_id = r.community_id
                    WHERE mp.meter_point_number = @meter AND mc.community_id = @community
                      AND mp.energy_direction = @direction
                    """, source);
                find.Parameters.AddWithValue("meter", point.MeteringPointCode);
                find.Parameters.AddWithValue("community", communityId);
                find.Parameters.AddWithValue("direction", point.EnergyDirection);
                readings.Add(point.Id, await find.ExecuteScalarAsync() is DateTime value
                    ? new DateTimeOffset(value, TimeSpan.Zero)
                    : throw new InvalidOperationException(
                        $"No {point.EnergyDirection} reading exists for meter {point.MeteringPointCode} in {communityId}."));
            }
        }
        var reading = readings.Values.Max();

        using var persistence = new HttpClient { BaseAddress = new Uri(persistenceUrl.TrimEnd('/') + "/") };
        var measurements = new TenantMeasurementProvisioner(persistence, Options.Create(new MeasurementOptions
        {
            Enabled = true, ServiceToken = token, GrafanaDatabaseHost = grafanaDbHost,
            GrafanaDatabasePort = 5432, GrafanaDatabaseName = dbConnection.Database!,
            ApiBaseUrl = persistenceUrl
        }));
        using var grafana = new HttpClient { BaseAddress = new Uri(grafanaUrl.TrimEnd('/') + "/") };
        grafana.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{grafanaUser}:{grafanaPassword}")));
        var provisioner = new GrafanaProvisioner(grafana, Options.Create(new GrafanaOptions
        { AdminUser = grafanaUser, AdminPassword = grafanaPassword }), db, measurements);
        using var ssoResponse = await grafana.GetAsync("api/v1/sso-settings/generic_oauth");
        ssoResponse.EnsureSuccessStatusCode();
        var originalSso = (await ssoResponse.Content.ReadFromJsonAsync<JsonObject>())?["settings"]?.AsObject()
            ?? throw new InvalidOperationException("Grafana SSO settings are unavailable.");

        long? orgId = null;
        var attempted = false;
        var completed = false;
        try
        {
            await using (var transaction = await db.LockCustomerAsync(customerId, default))
            {
                attempted = true;
                orgId = await provisioner.ReconcileAsync(customer, null, default);
                await transaction.CommitAsync();
            }
            var uid = ResourceIdentity.Dashboard(customerId, "datasource", customerId);
            using var sourceRequest = new HttpRequestMessage(HttpMethod.Get, $"api/datasources/uid/{uid}");
            sourceRequest.Headers.Add("X-Grafana-Org-Id", orgId.Value.ToString());
            using var sourceResponse = await grafana.SendAsync(sourceRequest);
            sourceResponse.EnsureSuccessStatusCode();
            var dataSource = await sourceResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal($"grafana_customer_{customerId:N}", dataSource.GetProperty("user").GetString());
            Assert.Equal(dbConnection.Database, dataSource.GetProperty("database").GetString());
            Assert.True(dataSource.GetProperty("secureJsonFields").GetProperty("password").GetBoolean());

            var meterDashboards = new List<(MeteringPoint Point, JsonElement Dashboard, JsonElement[] Panels)>();
            foreach (var point in customer.MeteringPoints!)
            {
                var dashboardUid = ResourceIdentity.Dashboard(customerId, "meter", point.Id);
                using var dashboardRequest = new HttpRequestMessage(HttpMethod.Get, $"api/dashboards/uid/{dashboardUid}");
                dashboardRequest.Headers.Add("X-Grafana-Org-Id", orgId.Value.ToString());
                using var dashboardResponse = await grafana.SendAsync(dashboardRequest);
                dashboardResponse.EnsureSuccessStatusCode();
                var dashboard = await dashboardResponse.Content.ReadFromJsonAsync<JsonElement>();
                var panels = dashboard.GetProperty("dashboard").GetProperty("panels").EnumerateArray().ToArray();
                var sql = panels.First(p => p.GetProperty("type").GetString() == "timeseries")
                    .GetProperty("targets")[0].GetProperty("rawSql").GetString()!;
                Assert.Contains($"grafana_customer_{customerId:N}.raw_measurements", sql);
                using var query = new HttpRequestMessage(HttpMethod.Post, "api/ds/query");
                query.Headers.Add("X-Grafana-Org-Id", orgId.Value.ToString());
                query.Content = JsonContent.Create(new
                {
                    from = readings[point.Id].AddHours(-1).ToUnixTimeMilliseconds().ToString(),
                    to = readings[point.Id].AddHours(1).ToUnixTimeMilliseconds().ToString(),
                    queries = new[] { new
                    {
                        refId = "A", datasource = new { uid }, rawSql = sql, format = "time_series",
                        rawQuery = true, editorMode = "code", intervalMs = 3600000, maxDataPoints = 100
                    } }
                });
                using var queryResponse = await grafana.SendAsync(query);
                queryResponse.EnsureSuccessStatusCode();
                var result = (await queryResponse.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("results").GetProperty("A");
                Assert.False(result.TryGetProperty("error", out var error), error.ToString());
                Assert.Contains(result.GetProperty("frames").EnumerateArray(),
                    frame => frame.GetProperty("data").GetProperty("values")[0].GetArrayLength() > 0);
                meterDashboards.Add((point, dashboard, panels));
            }
            var communityUid = ResourceIdentity.Dashboard(customerId, "community",
                customer.EnergyCommunities![0].Id);
            using var communityRequest = new HttpRequestMessage(HttpMethod.Get,
                $"api/dashboards/uid/{communityUid}");
            communityRequest.Headers.Add("X-Grafana-Org-Id", orgId.Value.ToString());
            using var communityResponse = await grafana.SendAsync(communityRequest);
            communityResponse.EnsureSuccessStatusCode();
            var communityDashboard = await communityResponse.Content.ReadFromJsonAsync<JsonElement>();
            var communityPanels = communityDashboard.GetProperty("dashboard").GetProperty("panels")
                .EnumerateArray().ToArray();
            Assert.StartsWith("Energiegemeinschaft · Übersicht (gesamt)",
                communityDashboard.GetProperty("dashboard").GetProperty("title").GetString());
            Assert.All(communityPanels, panel =>
            {
                var sql = panel.GetProperty("targets")[0].GetProperty("rawSql").GetString()!;
                Assert.Contains($"grafana_customer_{customerId:N}.community_hourly_measurements", sql);
                Assert.DoesNotContain("raw_measurements", sql);
                Assert.Contains("r.community_id =", sql);
                Assert.Contains("r.energy_direction =", sql);
            });
            Assert.Equal(12, communityPanels.Count(p => p.GetProperty("type").GetString() == "stat"));
            Assert.Equal(2, communityPanels.Count(p => p.GetProperty("type").GetString() == "barchart"));
            Assert.Single(communityPanels, p => p.GetProperty("type").GetString() == "heatmap");
            Assert.Single(communityPanels, p => p.GetProperty("title").GetString()!.Contains("Vergleich mit Vorwoche")
                && p.GetProperty("targets").GetArrayLength() == 2 && p.GetProperty("title").GetString()!.Contains("Verbrauch"));
            Assert.Single(meterDashboards.Where(item => item.Point.EnergyDirection == "consumption")
                .SelectMany(item => item.Panels), p => p.GetProperty("type").GetString() == "heatmap");
            using var preferencesRequest = new HttpRequestMessage(HttpMethod.Get, "api/org/preferences");
            preferencesRequest.Headers.Add("X-Grafana-Org-Id", orgId.Value.ToString());
            using var preferencesResponse = await grafana.SendAsync(preferencesRequest);
            preferencesResponse.EnsureSuccessStatusCode();
            Assert.Equal(communityUid, (await preferencesResponse.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("homeDashboardUID").GetString());
            foreach (var panel in meterDashboards.SelectMany(item => item.Panels)
                     .Concat(communityPanels).Where(p =>
                         p.GetProperty("type").GetString() is "stat" or "barchart" or "heatmap" ||
                         p.GetProperty("title").GetString()!.Contains("Vergleich mit Vorwoche") ||
                         p.GetProperty("title").GetString()!.Contains("Gemeinschaft gesamt ·") &&
                         p.GetProperty("title").GetString()!.Contains("Netz vs. EG")))
            {
                using var panelQuery = new HttpRequestMessage(HttpMethod.Post, "api/ds/query");
                panelQuery.Headers.Add("X-Grafana-Org-Id", orgId.Value.ToString());
                panelQuery.Content = JsonContent.Create(new
                {
                    from = reading.AddHours(-1).ToUnixTimeMilliseconds().ToString(),
                    to = reading.AddHours(1).ToUnixTimeMilliseconds().ToString(),
                    queries = panel.GetProperty("targets").EnumerateArray().Select(target => new
                    {
                        refId = target.GetProperty("refId").GetString(), datasource = new { uid },
                        rawSql = target.GetProperty("rawSql").GetString(),
                        format = target.GetProperty("format").GetString(),
                        rawQuery = true, editorMode = "code", intervalMs = 900000, maxDataPoints = 100
                    }).ToArray()
                });
                using var panelResponse = await grafana.SendAsync(panelQuery);
                if (!panelResponse.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Panel {panel.GetProperty("title").GetString()} returned {(int)panelResponse.StatusCode}: " +
                        await panelResponse.Content.ReadAsStringAsync());
                var results = (await panelResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
                foreach (var target in panel.GetProperty("targets").EnumerateArray())
                {
                    var panelResult = results.GetProperty(target.GetProperty("refId").GetString()!);
                    Assert.False(panelResult.TryGetProperty("error", out var panelError),
                        $"{panel.GetProperty("title").GetString()}: {panelError}");
                    if (panel.GetProperty("type").GetString() == "heatmap" ||
                        panel.GetProperty("title").GetString()!.Contains("Vergleich mit Vorwoche") ||
                        panel.GetProperty("title").GetString()!.Contains("Gemeinschaft gesamt ·") &&
                        panel.GetProperty("title").GetString()!.Contains("Netz vs. EG"))
                        Assert.Contains(panelResult.GetProperty("frames").EnumerateArray(),
                            frame => frame.GetProperty("data").GetProperty("values")[0].GetArrayLength() > 0);
                }
            }
            if (keepResources)
            {
                output.WriteLine($"Test customer ID: {customerId:D}");
                output.WriteLine($"Grafana organization ID: {orgId.Value}");
                foreach (var (point, dashboard, _) in meterDashboards)
                {
                    var dashboardPath = dashboard.GetProperty("meta").GetProperty("url").GetString()
                        ?? throw new InvalidOperationException("Grafana did not return a dashboard URL.");
                    var dashboardUrl = new Uri(new Uri(grafanaUrl.TrimEnd('/') + "/"), dashboardPath);
                    output.WriteLine($"{point.EnergyDirection} dashboard: {dashboardUrl}?orgId={orgId.Value}&from={readings[point.Id].AddHours(-1).ToUnixTimeMilliseconds()}&to={readings[point.Id].AddHours(1).ToUnixTimeMilliseconds()}");
                }
                var communityPath = communityDashboard.GetProperty("meta").GetProperty("url").GetString()
                    ?? throw new InvalidOperationException("Grafana did not return a community dashboard URL.");
                var communityUrl = new Uri(new Uri(grafanaUrl.TrimEnd('/') + "/"), communityPath);
                output.WriteLine($"Home dashboard: {communityUrl}?orgId={orgId.Value}&from={reading.AddDays(-7).ToUnixTimeMilliseconds()}&to={reading.AddHours(1).ToUnixTimeMilliseconds()}");
            }
            completed = true;
        }
        finally
        {
            try
            {
                if (attempted && !(keepResources && completed))
                {
                    using var named = await grafana.GetAsync(
                        $"api/orgs/name/{Uri.EscapeDataString(ResourceIdentity.OrganizationName(customer))}");
                    using var legacy = named.StatusCode == HttpStatusCode.NotFound
                        ? await grafana.GetAsync($"api/orgs/name/{ResourceIdentity.Organization(customerId)}")
                        : null;
                    var status = legacy?.StatusCode ?? named.StatusCode;
                    if (status == HttpStatusCode.OK)
                    {
                        await using var transaction = await db.LockCustomerAsync(customerId, default);
                        await provisioner.DeleteAsync(customerId, orgId, default);
                        await transaction.CommitAsync();
                    }
                    else if (status == HttpStatusCode.NotFound)
                    {
                        await using var source = new NpgsqlConnection(dbConnection.ConnectionString);
                        await source.OpenAsync();
                        await using var check = new NpgsqlCommand(
                            "SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname=@schema)", source);
                        check.Parameters.AddWithValue("schema", $"grafana_customer_{customerId:N}");
                        if (await check.ExecuteScalarAsync() is true)
                            await measurements.DeleteAsync(customerId, default);
                    }
                    else (legacy ?? named).EnsureSuccessStatusCode();
                }
            }
            finally
            {
                if (attempted && !(keepResources && completed))
                    await RestoreSsoAsync(grafana, originalSso);
            }
        }
    }

    private static async Task RestoreSsoAsync(HttpClient grafana, JsonObject settings)
    {
        using var response = await grafana.PatchAsJsonAsync("api/v1/sso-settings/generic_oauth", new
        {
            settings = new
            {
                org_mapping = settings["orgMapping"]?.GetValue<string>() ?? "",
                org_attribute_path = settings["orgAttributePath"]?.GetValue<string>(),
                role_attribute_path = settings["roleAttributePath"]?.GetValue<string>(),
                role_attribute_strict = settings["roleAttributeStrict"]?.GetValue<bool>(),
                skip_org_role_sync = settings["skipOrgRoleSync"]?.GetValue<bool>(),
                allow_assign_grafana_admin = settings["allowAssignGrafanaAdmin"]?.GetValue<bool>()
            }
        });
        response.EnsureSuccessStatusCode();
    }
}
