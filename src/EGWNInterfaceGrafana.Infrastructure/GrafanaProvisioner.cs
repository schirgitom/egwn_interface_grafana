using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed class GrafanaProvisioner(
    HttpClient client, IOptions<GrafanaOptions> options, ServiceDbContext db,
    TenantMeasurementProvisioner? measurements = null) : IGrafanaProvisioner
{
    public async Task<long> ReconcileAsync(CustomerSnapshot customer, long? organizationId, CancellationToken ct)
    {
        var source = measurements is { Enabled: true }
            ? await measurements.ReconcileAsync(customer, ct) : null;
        var orgId = await FindOrganizationAsync(customer.Id, ct);
        if (orgId is null)
        {
            using var created = await SendAsync(HttpMethod.Post, "api/orgs", null,
                new { name = ResourceIdentity.Organization(customer.Id) }, ct, HttpStatusCode.Conflict);
            // A prior crash or concurrent provisioning can leave the organization already created.
            orgId = await FindOrganizationAsync(customer.Id, ct)
                ?? throw new HttpRequestException("Grafana organization creation was not visible.");
        }
        if (organizationId is { } stored && stored != orgId)
            throw new InvalidEventException("Grafana organization identity changed; administrative recovery required.");

        if (source is not null) await EnsureDataSourceAsync(customer.Id, orgId.Value, source, ct);
        var dashboards = BuildDashboards(customer, source);
        foreach (var dashboard in dashboards)
        {
            using var response = await SendAsync(HttpMethod.Post, "api/dashboards/db", orgId,
                new { dashboard, overwrite = true, message = "EGWN customer reconciliation" }, ct);
        }

        var desired = dashboards.Select(d => d["uid"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var obsolete = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(HttpMethod.Get,
                $"api/search?type=dash-db&tag={ResourceIdentity.OwnerTag(customer.Id)}&limit=1000&page={page}", orgId, null, ct);
            var found = await response.Content.ReadFromJsonAsync<List<SearchResult>>(EventPolicy.Json, ct) ?? [];
            foreach (var item in found)
            {
                if (!item.Uid.StartsWith("egwn-", StringComparison.Ordinal) || desired.Contains(item.Uid)) continue;
                obsolete.Add(item.Uid);
            }
            if (found.Count < 1000) break;
        }
        foreach (var uid in obsolete)
        {
            using var deleted = await SendAsync(HttpMethod.Delete,
                $"api/dashboards/uid/{Uri.EscapeDataString(uid)}", orgId, null, ct, HttpStatusCode.NotFound);
        }
        await SynchronizeOrgMappingAsync(ct);
        if (source is not null) await measurements!.EnableAsync(customer.CustomerNumber, customer.Id, ct);
        return orgId.Value;
    }

    public async Task DeleteAsync(Guid customerId, long? organizationId, CancellationToken ct)
    {
        var actual = await FindOrganizationAsync(customerId, ct);
        if (actual is null)
        {
            await DeleteTenantAsync(customerId, ct);
            await SynchronizeOrgMappingAsync(ct);
            return;
        }
        if (organizationId is { } stored && stored != actual)
            throw new InvalidEventException("Refusing to delete an unexpected organization.");
        await DeleteTenantAsync(customerId, ct);
        using var response = await SendAsync(HttpMethod.Delete, $"api/orgs/{actual}", null, null, ct, HttpStatusCode.NotFound);
        await SynchronizeOrgMappingAsync(ct);
    }

    private async Task DeleteTenantAsync(Guid customerId, CancellationToken ct)
    {
        if (measurements is not { Enabled: true }) return;
        var state = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId, ct);
        var customer = System.Text.Json.JsonSerializer.Deserialize<CustomerSnapshot>(state.SnapshotJson, EventPolicy.Json)
            ?? throw new InvalidEventException("Customer snapshot missing on deletion.");
        await measurements.DeleteAsync(customer.CustomerNumber, customerId, ct);
    }

    private async Task EnsureDataSourceAsync(Guid customerId, long orgId, TenantDataSource source, CancellationToken ct)
    {
        var uid = ResourceIdentity.Dashboard(customerId, "datasource", customerId);
        using var existing = await SendAsync(HttpMethod.Get, $"api/datasources/uid/{uid}",
            orgId, null, ct, HttpStatusCode.NotFound);
        var settings = new
        {
            uid, name = "EGWN customer measurements", type = "grafana-postgresql-datasource",
            access = "proxy", url = $"{source.Host}:{source.Port}", database = source.Database,
            user = source.User, isDefault = true,
            jsonData = new { sslmode = "disable", postgresVersion = 1700, timescaledb = true },
            secureJsonData = new { password = source.Password }
        };
        if (existing.StatusCode != HttpStatusCode.NotFound)
        {
            var current = await existing.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            if (current?["orgId"]?.GetValue<long>() != orgId)
                throw new InvalidEventException("Existing datasource belongs to a different organization.");
        }
        using var saved = await SendAsync(existing.StatusCode == HttpStatusCode.NotFound ? HttpMethod.Post : HttpMethod.Put,
            existing.StatusCode == HttpStatusCode.NotFound ? "api/datasources" : $"api/datasources/uid/{uid}",
            orgId, settings, ct);
        using var verified = await SendAsync(HttpMethod.Get, $"api/datasources/uid/{uid}", orgId, null, ct);
        var result = await verified.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        if (result?["orgId"]?.GetValue<long>() != orgId ||
            result["user"]?.GetValue<string>() != source.User ||
            result["database"]?.GetValue<string>() != source.Database)
            throw new HttpRequestException("Grafana did not persist the customer-scoped datasource.");
    }

    private async Task SynchronizeOrgMappingAsync(CancellationToken ct)
    {
        // All callers already hold a customer transaction; serialize changes to the shared SSO configuration.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(617302947146132)", ct);
        var mappings = new List<string>();
        for (var page = 0; ; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, $"api/orgs?perpage=1000&page={page}", null, null, ct);
            var organizations = await response.Content.ReadFromJsonAsync<List<NamedOrganization>>(EventPolicy.Json, ct) ?? [];
            foreach (var org in organizations)
                if (org.Name.StartsWith("egwn-", StringComparison.Ordinal) &&
                    Guid.TryParseExact(org.Name[5..], "N", out _))
                    mappings.Add($"{org.Name}:{org.Id}:Viewer");
            if (organizations.Count < 1000) break;
        }
        using var updated = await SendAsync(HttpMethod.Patch, "api/v1/sso-settings/generic_oauth", null,
            new
            {
                settings = new
                {
                    org_mapping = string.Join(" ", mappings.Order(StringComparer.Ordinal)),
                    org_attribute_path = "[egwn_org]",
                    role_attribute_path = "'None'",
                    role_attribute_strict = true,
                    skip_org_role_sync = false,
                    allow_assign_grafana_admin = false
                }
            }, ct);
        using var check = await SendAsync(HttpMethod.Get, "api/v1/sso-settings/generic_oauth", null, null, ct);
        var stored = await check.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
        if (stored?["settings"]?["orgMapping"]?.GetValue<string>() != string.Join(" ", mappings.Order(StringComparer.Ordinal)))
            throw new HttpRequestException("Grafana did not persist the expected organization mapping.");
    }

    private async Task<long?> FindOrganizationAsync(Guid customerId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"api/orgs/name/{ResourceIdentity.Organization(customerId)}", null, null, ct, HttpStatusCode.NotFound);
        return response.StatusCode == HttpStatusCode.NotFound ? null :
            (await response.Content.ReadFromJsonAsync<OrganizationResult>(EventPolicy.Json, ct))?.Id;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, long? orgId, object? body, CancellationToken ct,
        HttpStatusCode? accepted = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{options.Value.AdminUser}:{options.Value.AdminPassword}")));
        if (orgId.HasValue) request.Headers.Add("X-Grafana-Org-Id", orgId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (body is not null) request.Content = JsonContent.Create(body, options: EventPolicy.Json);
        var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != accepted)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Grafana request failed with status {(int)status}.", null, status);
        }
        return response;
    }

    public static List<JsonObject> BuildDashboards(CustomerSnapshot customer, TenantDataSource? source = null)
    {
        var overviewUid = ResourceIdentity.Dashboard(customer.Id, "customer", customer.Id);
        var result = new List<JsonObject>();
        var points = customer.MeteringPoints ?? [];
        var communities = customer.EnergyCommunities ?? [];
        var summary = new StringBuilder();
        summary.AppendLine($"# {Escape(customer.Name)}").AppendLine()
            .AppendLine($"Customer: {Escape(customer.CustomerNumber)}").AppendLine()
            .AppendLine(source is null ? "Measurement queries are not connected yet." :
                "Measurements are limited to this customer and their authorized communities.").AppendLine();
        foreach (var point in points)
        {
            var uid = ResourceIdentity.Dashboard(customer.Id, "meter", point.Id);
            summary.AppendLine($"- [{Escape(point.MeteringPointCode)}](/d/{uid})");
            var meterDashboard = Dashboard(customer, uid, $"Meter - {point.MeteringPointCode}",
                $"{Escape(point.Description ?? point.MeteringPointCode)}\n\nDirection: {Escape(point.EnergyDirection)}" +
                (source is null ? "\n\nMeasurement queries are not connected yet." : ""),
                overviewUid);
            if (source is not null)
            {
                var memberCommunities = communities.Where(c => c.MeteringPoints.Any(p => p.Id == point.Id)).ToList();
                var panels = (JsonArray)meterDashboard["panels"]!;
                foreach (var community in memberCommunities)
                {
                    var start = 18 + (panels.Count - 1) / 5 * 13;
                    var scope = $"""
                        FROM {source.Schema}.raw_measurements r
                        JOIN {source.Schema}.meter_points mp ON mp."Id" = r.meter_id
                        WHERE $__timeFilter(r.timestamp) AND r.community_id = {SqlText(community.SourceId!)}
                          AND mp.meter_point_number = {SqlText(point.MeteringPointCode)}
                        """;
                    var generated = point.EnergyDirection == "generation";
                    var statNames = generated
                        ? new[] { "Erzeugung", "Netzeinspeisung", "Abgabe an EG", "EG-Anteil" }
                        : new[] { "Verbrauch", "Netzbezug", "Bezug aus EG", "EG-Deckungsgrad" };
                    var expressions = new[]
                    {
                        "SUM(r.total_consumption)", "SUM(r.grid_share)", "SUM(r.community_share)",
                        "SUM(r.community_share) / NULLIF(SUM(r.total_consumption), 0)"
                    };
                    for (var index = 0; index < expressions.Length; index++)
                        panels.Add(StatPanel(panels.Count + 1, $"{community.Name} - {statNames[index]}",
                            $"SELECT {expressions[index]} AS \"value\" {scope}", customer.Id,
                            index == 3 ? "percentunit" : "kwatth", start, index * 6));
                    var sql = $"""
                        SELECT date_bin($__interval_ms * INTERVAL '1 millisecond',
                          r.timestamp, TIMESTAMPTZ '2000-01-01') AS "time",
                          SUM(r.total_consumption) AS "Gesamt",
                          SUM(r.grid_share) AS "Netz",
                          SUM(r.community_share) AS "Energiegemeinschaft"
                        {scope}
                        GROUP BY 1 ORDER BY 1
                        """;
                    panels.Add(MeasurementPanel(panels.Count + 1, $"{community.Name} - " +
                        (generated ? "Erzeugung" : "Bezug"),
                        sql, customer.Id, "kwatth", start + 4));
                }
            }
            result.Add(meterDashboard);
        }
        foreach (var community in communities)
        {
            var uid = ResourceIdentity.Dashboard(customer.Id, "community", community.Id);
            summary.AppendLine($"- [{Escape(community.Name)}](/d/{uid})");
            var text = new StringBuilder($"# {Escape(community.Name)}\n\n");
            if (source is null) text.AppendLine("Measurement queries are not connected yet.").AppendLine();
            foreach (var point in community.MeteringPoints)
                text.AppendLine($"- {Escape(point.MeteringPointCode)}: {point.ParticipationPercentage.ToString(System.Globalization.CultureInfo.InvariantCulture)}%");
            var communityDashboard = Dashboard(customer, uid, $"Community - {community.Name}", text.ToString(), overviewUid);
            if (source is not null)
            {
                var panels = (JsonArray)communityDashboard["panels"]!;
                foreach (var (direction, index) in new[] { "consumption", "generation" }.Select((d, i) => (d, i)))
                {
                    var scope = $"""
                        FROM {source.Schema}.community_totals
                        WHERE $__timeFilter(timestamp) AND community_id = {SqlText(community.SourceId!)}
                          AND energy_direction = {SqlText(direction)}
                        """;
                    panels.Add(StatPanel(panels.Count + 1,
                        direction == "consumption" ? "Verbrauch der Gemeinschaft" : "Erzeugung der Gemeinschaft",
                        $"SELECT SUM(total) AS \"value\" {scope}", customer.Id, "kwatth", 18, index * 12, 12));
                    var sql = $"""
                        SELECT date_bin($__interval_ms * INTERVAL '1 millisecond',
                          timestamp, TIMESTAMPTZ '2000-01-01') AS "time",
                          SUM(total) AS "Gesamt", SUM(grid_share) AS "Netz",
                          SUM(community_share) AS "Energiegemeinschaft"
                        {scope}
                        GROUP BY 1 ORDER BY 1
                        """;
                    panels.Add(MeasurementPanel(panels.Count + 1,
                        direction == "consumption" ? "Gesamter Bezug der Energiegemeinschaft" :
                        "Gesamte Erzeugung der Energiegemeinschaft", sql, customer.Id, "kwatth",
                        22 + index * 9));
                }
            }
            result.Add(communityDashboard);
        }
        result.Add(Dashboard(customer, overviewUid, $"{customer.CustomerNumber} - {customer.Name}", summary.ToString(), overviewUid));
        return result;
    }

    private static string SqlText(string value) =>
        $"convert_from(decode('{Convert.ToHexString(Encoding.UTF8.GetBytes(value))}', 'hex'), 'UTF8')";

    private static JsonObject StatPanel(int id, string title, string sql, Guid customerId, string unit, int y,
        int x, int width = 6)
    {
        var panel = MeasurementPanel(id, title, sql, customerId, unit, y);
        panel["type"] = "stat";
        panel["gridPos"] = new JsonObject { ["h"] = 4, ["w"] = width, ["x"] = x, ["y"] = y };
        panel["targets"]![0]!["format"] = "table";
        panel["options"] = new JsonObject
        {
            ["reduceOptions"] = new JsonObject { ["calcs"] = new JsonArray("lastNotNull"),
                ["fields"] = "", ["values"] = false }
        };
        return panel;
    }

    private static JsonObject MeasurementPanel(int id, string title, string sql, Guid customerId, string unit, int y)
    {
        var uid = ResourceIdentity.Dashboard(customerId, "datasource", customerId);
        return new JsonObject
        {
            ["id"] = id, ["title"] = title, ["type"] = "timeseries",
            ["gridPos"] = new JsonObject { ["h"] = 9, ["w"] = 24, ["x"] = 0, ["y"] = y },
            ["datasource"] = new JsonObject { ["type"] = "grafana-postgresql-datasource", ["uid"] = uid },
            ["targets"] = new JsonArray(new JsonObject
            {
                ["refId"] = "A", ["rawQuery"] = true, ["editorMode"] = "code",
                ["format"] = "time_series", ["rawSql"] = sql,
                ["datasource"] = new JsonObject { ["type"] = "grafana-postgresql-datasource", ["uid"] = uid }
            }),
            ["fieldConfig"] = new JsonObject { ["defaults"] = new JsonObject { ["unit"] = unit } }
        };
    }

    private static JsonObject Dashboard(CustomerSnapshot customer, string uid, string title, string content, string home) =>
        new()
        {
            ["uid"] = uid, ["title"] = title, ["schemaVersion"] = 39,
            ["editable"] = false,
            ["tags"] = new JsonArray(ResourceIdentity.OwnerTag(customer.Id)),
            ["links"] = new JsonArray(new JsonObject { ["title"] = "Customer overview", ["url"] = $"/d/{home}", ["type"] = "link" }),
            ["panels"] = new JsonArray(new JsonObject
            {
                ["id"] = 1, ["type"] = "text", ["title"] = "Customer information",
                ["gridPos"] = new JsonObject { ["h"] = 18, ["w"] = 24, ["x"] = 0, ["y"] = 0 },
                ["options"] = new JsonObject { ["mode"] = "markdown", ["content"] = content }
            }),
            ["time"] = new JsonObject { ["from"] = "now-30d", ["to"] = "now" }
        };

    private static string Escape(string value) =>
        WebUtility.HtmlEncode(value).Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")
            .Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("#", "\\#");

    private sealed record OrganizationResult(long Id);
    private sealed record SearchResult(string Uid);
    private sealed record NamedOrganization(long Id, string Name);
}
