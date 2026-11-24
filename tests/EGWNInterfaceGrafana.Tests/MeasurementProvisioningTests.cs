using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace EGWNInterfaceGrafana.Tests;

public sealed class MeasurementProvisioningTests
{
    [Fact]
    public async Task OriginalRabbitMqJsonSendsScopedPersistenceEntitlements()
    {
        var customer = EventPolicy.Parse(System.Text.Encoding.UTF8.GetBytes(
            PolicyTests.OriginalCustomerEventJson)).Customer;
        var calls = new List<(HttpMethod Method, string Path, string? Body)>();
        using var client = new HttpClient(new Handler(async request =>
        {
            calls.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync()));
            if (request.RequestUri.AbsolutePath.EndsWith("/credentials", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        username = $"grafana_customer_{customer.Id:N}",
                        schema = $"grafana_customer_{customer.Id:N}", password = "fake-test-password"
                    })
                };
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        })) { BaseAddress = new Uri("https://persistence.example.invalid/") };

        var source = await Create(client).ReconcileAsync(customer, default);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Put }, calls.Select(c => c.Method));
        Assert.Equal($"/internal/grafana/customers/{customer.Id:D}/credentials", calls[0].Path);
        Assert.Equal($"/internal/grafana/customers/{customer.Id:D}/entitlements", calls[1].Path);
        using var entitlements = JsonDocument.Parse(calls[1].Body!);
        Assert.Equal("RC101900",
            Assert.Single(entitlements.RootElement.GetProperty("communities").EnumerateArray()).GetString());
        var meters = entitlements.RootElement.GetProperty("meters").EnumerateArray().ToArray();
        Assert.Equal(2, meters.Length);
        Assert.All(meters, meter => Assert.Equal("RC101900", meter.GetProperty("communityId").GetString()));
        Assert.Equal(customer.MeteringPoints!.Select(p => p.MeteringPointCode).Order(),
            meters.Select(m => m.GetProperty("meterPointNumber").GetString()).Order());
        Assert.Equal($"grafana_customer_{customer.Id:N}", source.Schema);
        Assert.Equal("fake-test-password", source.Password);
        var dashboards = GrafanaProvisioner.BuildDashboards(customer, source);
        Assert.Equal(3, dashboards.Count);
        Assert.All(dashboards.Where(d => d["title"]!.GetValue<string>().StartsWith("Zählpunkt ·")),
            dashboard => Assert.Contains($"{source.Schema}.raw_measurements", dashboard["panels"]!.ToJsonString()));
        Assert.Contains($"{source.Schema}.community_hourly_measurements",
            dashboards.Single(d => d["title"]!.GetValue<string>().StartsWith("Energiegemeinschaft ·"))
                ["panels"]!.ToJsonString());
    }

    [Fact]
    public void CommunityOverviewSeparatesDirectionsAndUsesScopedCommunityTotals()
    {
        var customer = PolicyTests.Message().Customer;
        var schema = $"grafana_customer_{customer.Id:N}";
        var dashboards = GrafanaProvisioner.BuildDashboards(customer,
            new TenantDataSource(schema, schema, "unit-test-password", "egwn-timescaledb", 5432, "egwndata2"));
        var community = dashboards.Single(d => d["title"]!.GetValue<string>().StartsWith("Energiegemeinschaft ·"));
        var panels = community["panels"]!.AsArray();
        Assert.Equal(12, panels.Count(p => p!["type"]!.GetValue<string>() == "stat"));
        Assert.All(panels, p => Assert.NotEqual("text", p!["type"]!.GetValue<string>()));
        Assert.Contains("Übersicht (gesamt)", community["title"]!.GetValue<string>());
        Assert.Contains($"{schema}.community_hourly_measurements", panels.ToJsonString());
        Assert.DoesNotContain($"{schema}.raw_measurements", panels.ToJsonString());
        Assert.Contains("r.energy_direction", panels.ToJsonString());
        Assert.DoesNotContain("mp.meter_point_number", panels.ToJsonString());
        Assert.Contains("Stunden-Leistung", panels.ToJsonString());
        Assert.DoesNotContain(" * 4 AS", panels.ToJsonString());
        Assert.Equal(2, community["links"]!.AsArray().Count);
    }

    [Fact]
    public void PreviousWeekComparisonAndHourlyConsumptionHeatmapStayCustomerScoped()
    {
        var customer = EventPolicy.Parse(System.Text.Encoding.UTF8.GetBytes(
            PolicyTests.OriginalCustomerEventJson)).Customer;
        var schema = $"grafana_customer_{customer.Id:N}";
        var dashboards = GrafanaProvisioner.BuildDashboards(customer,
            new TenantDataSource(schema, schema, "unit-test-password", "egwn-timescaledb", 5432, "egwndata2"));
        foreach (var dashboard in dashboards)
        {
            var panels = dashboard["panels"]!.AsArray();
            var comparisons = panels.Where(p => p!["title"]!.GetValue<string>()
                .Contains("Vergleich mit Vorwoche")).ToArray();
            var isCommunity = dashboard["title"]!.GetValue<string>().StartsWith("Energiegemeinschaft");
            Assert.Equal(isCommunity ? 2 : 1, comparisons.Length);
            foreach (var comparison in comparisons)
            {
                var targets = comparison!["targets"]!.AsArray();
                Assert.Equal(new[] { "A", "B" }, targets.Select(t => t!["refId"]!.GetValue<string>()));
                var current = targets[0]!["rawSql"]!.GetValue<string>();
                var previous = targets[1]!["rawSql"]!.GetValue<string>();
                Assert.Contains("$__timeFilter(r.timestamp)", current);
                Assert.Contains("AS \"Aktuell\"", current);
                Assert.Contains("$__timeFrom()::timestamptz - INTERVAL '7 days'", previous);
                Assert.Contains("$__timeTo()::timestamptz - INTERVAL '7 days'", previous);
                Assert.Contains("r.timestamp + INTERVAL '7 days'", previous);
                Assert.Contains("AS \"Vorwoche\"", previous);
                Assert.Contains($"{schema}.{(isCommunity ? "community_hourly_measurements" : "raw_measurements")}",
                    previous);
                Assert.Contains("r.community_id =", previous);
                Assert.Contains(isCommunity ? "r.energy_direction" : "mp.meter_point_number", previous);
            }

            var isGenerationMeter = dashboard["title"]!.GetValue<string>().StartsWith("Zählpunkt · Erzeugung");
            if (isGenerationMeter)
                Assert.DoesNotContain(panels, p => p!["type"]!.GetValue<string>() == "heatmap");
            else
            {
                var heatmap = Assert.Single(panels, p => p!["type"]!.GetValue<string>() == "heatmap")!;
                Assert.False(heatmap["options"]!["calculate"]!.GetValue<bool>());
                Assert.Equal("none", heatmap["fieldConfig"]!["defaults"]!["unit"]!.GetValue<string>());
                Assert.Equal("kwatth", heatmap["options"]!["cellValues"]!["unit"]!.GetValue<string>());
                var sql = heatmap["targets"]![0]!["rawSql"]!.GetValue<string>();
                Assert.Contains("Europe/Vienna", sql);
                Assert.Contains("$__timeFilter(r.timestamp)", sql);
                Assert.Contains("SUM(r.total_consumption)", sql);
                Assert.Contains("r.community_id =", sql);
                Assert.Contains("AS \"0\"", sql);
                Assert.Contains("AS \"23\"", sql);
            }
        }
    }

    [Fact]
    public async Task RotatesCredentialsAndReplacesAllEntitlementsBeforeReturningDataSource()
    {
        var customer = PolicyTests.Message().Customer;
        var second = customer.EnergyCommunities![0] with
        { Id = Guid.NewGuid(), SourceId = "external-456" };
        customer = customer with { EnergyCommunities = [customer.EnergyCommunities[0], second] };
        var calls = new List<(HttpMethod Method, string Path, string? Token, string? Body)>();
        var rotations = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            calls.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync()));
            if (request.RequestUri.AbsolutePath.EndsWith("/credentials", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        username = $"grafana_customer_{customer.Id:N}", schema = $"grafana_customer_{customer.Id:N}",
                        password = $"rotated-{++rotations}"
                    })
                };
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        })) { BaseAddress = new Uri("https://persistence.example.invalid:8443/") };
        var provisioner = Create(client);
        var first = await provisioner.ReconcileAsync(customer, default);
        var updated = await provisioner.ReconcileAsync(customer with
        { MeteringPoints = [], EnergyCommunities = [second with { MeteringPoints = [] }] }, default);
        await provisioner.DeleteAsync(customer.Id, default);

        Assert.Equal($"grafana_customer_{customer.Id:N}", first.Schema);
        Assert.Equal(first.Schema, first.User);
        Assert.Equal("rotated-1", first.Password);
        Assert.Equal("rotated-2", updated.Password);
        Assert.Equal("egwn-timescaledb", updated.Host);
        Assert.Equal("egwndata2", updated.Database);
        var dashboards = GrafanaProvisioner.BuildDashboards(customer, first);
        Assert.Contains(first.Schema + ".raw_measurements", dashboards.Single(d =>
            d["title"]!.GetValue<string>().StartsWith("Zählpunkt ·"))["panels"]!.ToJsonString());
        Assert.All(dashboards.Where(d => d["title"]!.GetValue<string>().StartsWith("Energiegemeinschaft ·")),
            d => Assert.Contains(first.Schema + ".community_hourly_measurements", d["panels"]!.ToJsonString()));
        Assert.Equal(12, dashboards.Single(d => d["title"]!.GetValue<string>().StartsWith("Zählpunkt ·"))
            ["panels"]!.AsArray().Count(p => p!["type"]!.GetValue<string>() == "stat"));
        Assert.All(dashboards.Where(d => d["title"]!.GetValue<string>().StartsWith("Energiegemeinschaft ·")),
            d => Assert.Equal(12, d["panels"]!.AsArray().Count(p => p!["type"]!.GetValue<string>() == "stat")));
        Assert.DoesNotContain(first.Password, JsonSerializer.Serialize(dashboards));
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Post, HttpMethod.Put, HttpMethod.Post },
            calls.Select(c => c.Method));
        Assert.All(calls, c => Assert.Equal("Bearer example-service-token-0123456789012345", c.Token));
        Assert.Equal($"/internal/grafana/customers/{customer.Id:D}/credentials", calls[0].Path);
        Assert.Equal($"/internal/grafana/customers/{customer.Id:D}/entitlements", calls[1].Path);
        Assert.Equal($"/internal/grafana/customers/{customer.Id:D}/disable", calls[4].Path);
        using var entitlements = JsonDocument.Parse(calls[1].Body!);
        Assert.Equal(2, entitlements.RootElement.GetProperty("communities").GetArrayLength());
        var meters = entitlements.RootElement.GetProperty("meters").EnumerateArray().ToArray();
        Assert.Equal(2, meters.Length);
        Assert.Equal(2, meters.Select(m => m.GetProperty("communityId").GetString()).Distinct().Count());
        Assert.All(meters, m => Assert.Equal(customer.MeteringPoints![0].MeteringPointCode,
            m.GetProperty("meterPointNumber").GetString()));
        using var replaced = JsonDocument.Parse(calls[3].Body!);
        Assert.Single(replaced.RootElement.GetProperty("communities").EnumerateArray());
        Assert.Empty(replaced.RootElement.GetProperty("meters").EnumerateArray());
        Assert.DoesNotContain(first.Password, calls[1].Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedEntitlementsNeverReturnCredentials(HttpStatusCode failure)
    {
        var customer = PolicyTests.Message().Customer;
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            calls++;
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/credentials", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        username = $"grafana_customer_{customer.Id:N}", schema = $"grafana_customer_{customer.Id:N}",
                        password = "rotated-password"
                    })
                }
                : new HttpResponseMessage(failure));
        })) { BaseAddress = new Uri("https://persistence.example.invalid/") };
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Create(client).ReconcileAsync(customer, default));
        Assert.Equal(failure, exception.StatusCode);
        Assert.Contains($"Persistence provisioning request PUT https://persistence.example.invalid/internal/grafana/customers/{customer.Id:D}/entitlements",
            exception.Message);
        Assert.Contains($"HTTP {(int)failure} ({failure})", exception.Message);
        Assert.Contains("expected 204 (NoContent)", exception.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task HttpFailureDiagnosticsExcludeCredentialsAndResponseBodies()
    {
        using var client = new HttpClient(new Handler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"password\":\"response-secret\"}")
            })))
        { BaseAddress = new Uri("https://url-user:url-password@persistence.example.invalid/") };
        var customer = PolicyTests.Message().Customer;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Create(client).ReconcileAsync(customer, default));

        Assert.Contains($"POST https://persistence.example.invalid/internal/grafana/customers/{customer.Id:D}/credentials",
            exception.Message);
        Assert.Contains("HTTP 500 (InternalServerError)", exception.Message);
        Assert.DoesNotContain("url-user", exception.ToString());
        Assert.DoesNotContain("url-password", exception.ToString());
        Assert.DoesNotContain("response-secret", exception.ToString());
        Assert.DoesNotContain("example-service-token", exception.ToString());
    }

    [Fact]
    public async Task RejectsIncorrectCredentialsAndInvalidSnapshot()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { username = "other", schema = "public", password = "secret" }) });
        })) { BaseAddress = new Uri("https://persistence.example.invalid/") };
        var customer = PolicyTests.Message().Customer;
        await Assert.ThrowsAsync<InvalidEventException>(() => Create(client).ReconcileAsync(customer with
        { EnergyCommunities = [customer.EnergyCommunities![0] with { SourceId = "" }] }, default));
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).ReconcileAsync(customer, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisableFailureIsNotTreatedAsDeletion()
    {
        using var client = new HttpClient(new Handler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))))
        { BaseAddress = new Uri("https://persistence.example.invalid/") };
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Create(client).DeleteAsync(Guid.NewGuid(), default));
        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
    }

    private static TenantMeasurementProvisioner Create(HttpClient client) => new(client,
        Options.Create(new MeasurementOptions
        {
            Enabled = true, ServiceToken = "example-service-token-0123456789012345",
            GrafanaDatabaseHost = "egwn-timescaledb", GrafanaDatabaseName = "egwndata2"
        }));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            send(request);
    }
}
