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
        var orgId = await FindOrganizationAsync(customer.Id, organizationId,
            ResourceIdentity.OrganizationName(customer), ct);
        if (orgId is null)
        {
            using var created = await SendAsync(HttpMethod.Post, "api/orgs", null,
                new { name = ResourceIdentity.Organization(customer.Id) }, ct, HttpStatusCode.Conflict);
            // A prior crash or concurrent provisioning can leave the organization already created.
            orgId = await FindOrganizationAsync(customer.Id, null, ResourceIdentity.OrganizationName(customer), ct)
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
        var homeUid = customer.EnergyCommunities is { Count: > 0 } communities
            ? ResourceIdentity.Dashboard(customer.Id, "community", communities[0].Id)
            : ResourceIdentity.Dashboard(customer.Id, "customer", customer.Id);
        using (var saved = await SendAsync(HttpMethod.Put, "api/org/preferences", orgId,
            new { homeDashboardUID = homeUid }, ct))
        {
        }
        using (var verified = await SendAsync(HttpMethod.Get, "api/org/preferences", orgId, null, ct))
        {
            var preference = await verified.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct);
            if (preference?["homeDashboardUID"]?.GetValue<string>() != homeUid)
                throw new HttpRequestException("Grafana did not persist the organization home dashboard.");
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
        using (var renamed = await SendAsync(HttpMethod.Put, $"api/orgs/{orgId}", null,
            new { name = ResourceIdentity.OrganizationName(customer) }, ct))
        {
        }
        await SynchronizeOrgMappingAsync(ct, customer.Id, orgId.Value);
        return orgId.Value;
    }

    public async Task DeleteAsync(Guid customerId, long? organizationId, CancellationToken ct)
    {
        var actual = await FindOrganizationAsync(customerId, organizationId, null, ct);
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
        await measurements.DeleteAsync(customerId, ct);
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
            result["database"]?.GetValue<string>() != source.Database ||
            result["secureJsonFields"]?["password"]?.GetValue<bool>() != true)
            throw new HttpRequestException("Grafana did not persist the customer-scoped datasource.");
    }

    private async Task SynchronizeOrgMappingAsync(CancellationToken ct, Guid? activeCustomerId = null, long? activeOrgId = null)
    {
        // All callers already hold a customer transaction; serialize changes to the shared SSO configuration.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(617302947146132)", ct);
        var mappings = new List<string>();
        for (var page = 0; ; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, $"api/orgs?perpage=1000&page={page}", null, null, ct);
            var organizations = await response.Content.ReadFromJsonAsync<List<NamedOrganization>>(EventPolicy.Json, ct) ?? [];
            foreach (var org in organizations)
            {
                if (org.Name.StartsWith("egwn-", StringComparison.Ordinal) &&
                    Guid.TryParseExact(org.Name[5..], "N", out var legacyId))
                    mappings.Add($"{ResourceIdentity.Organization(legacyId)}:{org.Id}:Viewer");
                else
                {
                    var owner = activeOrgId == org.Id ? activeCustomerId : await FindDashboardOwnerAsync(org.Id, ct);
                    if (owner is { } customerId)
                        mappings.Add($"{ResourceIdentity.Organization(customerId)}:{org.Id}:Viewer");
                }
            }
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

    private async Task<long?> FindOrganizationAsync(Guid customerId, long? storedId, string? desiredName,
        CancellationToken ct)
    {
        if (storedId is { } id)
        {
            using var stored = await SendAsync(HttpMethod.Get, $"api/orgs/{id}", null, null, ct, HttpStatusCode.NotFound);
            if (stored.StatusCode != HttpStatusCode.NotFound) return id;
        }
        using var response = await SendAsync(HttpMethod.Get,
            $"api/orgs/name/{ResourceIdentity.Organization(customerId)}", null, null, ct, HttpStatusCode.NotFound);
        if (response.StatusCode != HttpStatusCode.NotFound)
            return (await response.Content.ReadFromJsonAsync<OrganizationResult>(EventPolicy.Json, ct))?.Id;

        if (desiredName is not null)
        {
            using var named = await SendAsync(HttpMethod.Get,
                $"api/orgs/name/{Uri.EscapeDataString(desiredName)}", null, null, ct, HttpStatusCode.NotFound);
            if (named.StatusCode == HttpStatusCode.NotFound) return null;
            var candidate = await named.Content.ReadFromJsonAsync<OrganizationResult>(EventPolicy.Json, ct)
                ?? throw new InvalidEventException("Grafana organization response is incomplete.");
            if (!await HasOwnedDashboardAsync(customerId, candidate.Id, ct))
                throw new InvalidEventException("Grafana organization name belongs to an unmanaged organization.");
            return candidate.Id;
        }

        long? match = null;
        for (var page = 0; ; page++)
        {
            using var listed = await SendAsync(HttpMethod.Get, $"api/orgs?perpage=1000&page={page}", null, null, ct);
            var organizations = await listed.Content.ReadFromJsonAsync<List<NamedOrganization>>(EventPolicy.Json, ct)
                ?? throw new InvalidEventException("Grafana organization list is unavailable.");
            foreach (var org in organizations)
            {
                if (!await HasOwnedDashboardAsync(customerId, org.Id, ct)) continue;
                if (match is not null)
                    throw new InvalidEventException("Multiple Grafana organizations contain customer dashboards.");
                match = org.Id;
            }
            if (organizations.Count < 1000) return match;
        }
    }

    private async Task<bool> HasOwnedDashboardAsync(Guid customerId, long orgId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"api/search?type=dash-db&tag={ResourceIdentity.OwnerTag(customerId)}&limit=1",
            orgId, null, ct);
        var dashboards = await response.Content.ReadFromJsonAsync<List<JsonObject>>(cancellationToken: ct)
            ?? throw new InvalidEventException("Grafana dashboard search is unavailable.");
        return dashboards.Count > 0;
    }

    private async Task<Guid?> FindDashboardOwnerAsync(long orgId, CancellationToken ct)
    {
        Guid? owner = null;
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(HttpMethod.Get,
                $"api/search?type=dash-db&limit=1000&page={page}", orgId, null, ct);
            var dashboards = await response.Content.ReadFromJsonAsync<List<SearchResult>>(EventPolicy.Json, ct)
                ?? throw new InvalidEventException("Grafana dashboard search is unavailable.");
            foreach (var tag in dashboards.SelectMany(d => d.Tags ?? []))
            {
                const string prefix = "egwn-managed-";
                if (!tag.StartsWith(prefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(tag[prefix.Length..], "N", out var customerId)) continue;
                if (owner is { } existing && existing != customerId)
                    throw new InvalidEventException("Grafana organization contains dashboards from multiple customers.");
                owner = customerId;
            }
            if (dashboards.Count < 1000) return owner;
        }
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
            var target = request.RequestUri!.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            throw new HttpRequestException(
                $"Grafana request {method} {target} failed with HTTP {(int)status} ({status}).", null, status);
        }
        return response;
    }

    public static List<JsonObject> BuildDashboards(CustomerSnapshot customer, TenantDataSource? source = null)
    {
        var overviewUid = ResourceIdentity.Dashboard(customer.Id, "customer", customer.Id);
        var result = new List<JsonObject>();
        var points = customer.MeteringPoints ?? [];
        var communities = customer.EnergyCommunities ?? [];
        var links = new JsonArray();
        foreach (var community in communities)
            links.Add(new JsonObject
            {
                ["title"] = $"EG · {community.Name}",
                ["url"] = $"/d/{ResourceIdentity.Dashboard(customer.Id, "community", community.Id)}",
                ["type"] = "link"
            });
        foreach (var point in points)
            links.Add(new JsonObject
            {
                ["title"] = $"Zählpunkt · {(point.EnergyDirection == "generation" ? "Erzeugung" : "Bezug")} · {point.MeteringPointCode}",
                ["url"] = $"/d/{ResourceIdentity.Dashboard(customer.Id, "meter", point.Id)}",
                ["type"] = "link"
            });
        foreach (var point in points)
        {
            var uid = ResourceIdentity.Dashboard(customer.Id, "meter", point.Id);
            var direction = point.EnergyDirection == "generation" ? "Erzeugung" : "Bezug";
            var meterDashboard = Dashboard(customer, uid, $"Zählpunkt · {direction} · {point.MeteringPointCode}",
                source is null ? "Messdaten sind noch nicht verbunden." : "", links);
            if (source is not null)
            {
                var memberCommunities = communities.Where(c => c.MeteringPoints.Any(p => p.Id == point.Id)).ToList();
                var panels = (JsonArray)meterDashboard["panels"]!;
                for (var communityIndex = 0; communityIndex < memberCommunities.Count; communityIndex++)
                {
                    var community = memberCommunities[communityIndex];
                    var start = communityIndex * (point.EnergyDirection == "generation" ? 31 : 43);
                    var scope = $"""
                        FROM {source.Schema}.raw_measurements r
                        JOIN {source.Schema}.meter_points mp ON mp."Id" = r.meter_id
                        WHERE $__timeFilter(r.timestamp) AND r.community_id = {SqlText(community.SourceId!)}
                          AND mp.meter_point_number = {SqlText(point.MeteringPointCode)}
                        """;
                    AddEnergyPanels(panels, customer.Id, scope, community.Name,
                        point.EnergyDirection == "generation", start);
                }
            }
            meterDashboard["description"] = $"{customer.Name} | {point.Description ?? point.MeteringPointCode}";
            result.Add(meterDashboard);
        }
        foreach (var community in communities)
        {
            var uid = ResourceIdentity.Dashboard(customer.Id, "community", community.Id);
            var communityDashboard = Dashboard(customer, uid,
                $"Energiegemeinschaft · Übersicht (gesamt) · {community.Name}",
                source is null ? "Messdaten sind noch nicht verbunden." : "", links);
            communityDashboard["description"] =
                $"{community.Name} | Aggregierte Messdaten aller freigegebenen Zählpunkte";
            if (source is not null)
            {
                var panels = (JsonArray)communityDashboard["panels"]!;
                var start = 0;
                foreach (var direction in new[] { "generation", "consumption" })
                {
                    var scope = $"""
                        FROM {source.Schema}.community_hourly_measurements r
                        WHERE $__timeFilter(r.timestamp) AND r.community_id = {SqlText(community.SourceId!)}
                          AND r.energy_direction = {SqlText(direction)}
                        """;
                    AddEnergyPanels(panels, customer.Id, scope, "Gemeinschaft gesamt",
                        direction == "generation", start, hourly: true);
                    start += direction == "generation" ? 31 : 43;
                }
            }
            result.Add(communityDashboard);
        }
        if (communities.Count == 0)
            result.Add(Dashboard(customer, overviewUid, "Energiegemeinschaften · Übersicht",
                "Noch keine Energiegemeinschaft zugeordnet.", links));
        return result;
    }

    private static void AddEnergyPanels(JsonArray panels, Guid customerId, string scope, string label,
        bool generation, int y, bool hourly = false)
    {
        var bucketSize = hourly ? "1 hour" : "15 minutes";
        var powerMultiplier = hourly ? "" : " * 4";
        var localHour = hourly ? "bucket AT TIME ZONE 'Europe/Vienna'" : "bucket";
        var names = generation
            ? new[] { "Erzeugung", "Netzeinspeisung", "Abgabe an EG", "EG-Anteil", "Spitzenleistung", "Ø Leistung" }
            : new[] { "Verbrauch", "Netzbezug", "Bezug aus EG", "EG-Deckungsgrad", "Spitzenleistung", "Ø Leistung" };
        var expressions = new[]
        {
            "SUM(r.total_consumption)", "SUM(r.grid_share)", "SUM(r.community_share)",
            "SUM(r.community_share) / NULLIF(SUM(r.total_consumption), 0)"
        };
        for (var index = 0; index < names.Length; index++)
        {
            var sql = index < 4 ? $"SELECT {expressions[index]} AS \"value\" {scope}" : $"""
                SELECT {(index == 4 ? "MAX" : "AVG")}(bucket_energy){powerMultiplier} AS "value"
                FROM (
                  SELECT date_bin(INTERVAL '{bucketSize}', r.timestamp,
                    TIMESTAMPTZ '2000-01-01') AS bucket, SUM(r.total_consumption) AS bucket_energy
                  {scope} GROUP BY 1
                ) intervals
                """;
            panels.Add(StatPanel(panels.Count + 1, $"{label} · {names[index]}",
                sql, customerId,
                index == 3 ? "percentunit" : index >= 4 ? "kwatt" : "kwatth",
                y, index * 4, 4));
        }
        panels.Add(MeasurementPanel(panels.Count + 1,
            $"{label} · {(generation ? "Erzeugung" : "Verbrauch")} · Netz vs. EG",
            $"""
            SELECT date_bin($__interval_ms * INTERVAL '1 millisecond',
              r.timestamp, TIMESTAMPTZ '2000-01-01') AS "time",
              SUM(r.grid_share) AS "Netz", SUM(r.community_share) AS "Energiegemeinschaft"
            {scope} GROUP BY 1 ORDER BY 1
            """, customerId, "kwatth", y + 4, 0, 12));
        panels.Add(MeasurementPanel(panels.Count + 1, $"{label} · EG-Anteil im Verlauf",
            $"""
            SELECT date_bin($__interval_ms * INTERVAL '1 millisecond',
              r.timestamp, TIMESTAMPTZ '2000-01-01') AS "time",
              SUM(r.community_share) / NULLIF(SUM(r.total_consumption), 0) AS "EG-Anteil"
            {scope} GROUP BY 1 ORDER BY 1
            """, customerId, "percentunit", y + 4, 12, 12));
        panels.Add(MeasurementPanel(panels.Count + 1,
            $"{label} · {(hourly ? "Stunden-Leistung" : "Viertelstunden-Leistung")}",
            $"""
            SELECT date_bin(INTERVAL '{bucketSize}', r.timestamp,
              TIMESTAMPTZ '2000-01-01') AS "time",
              SUM(r.total_consumption){powerMultiplier} AS "Ø Leistung"
            {scope} GROUP BY 1 ORDER BY 1
            """, customerId, "kwatt", y + 13, 0, 12));
        panels.Add(BarPanel(panels.Count + 1, $"{label} · Ø Stundenprofil{(hourly ? " (Wien)" : "")}",
            $"""
            SELECT LPAD(EXTRACT(HOUR FROM {localHour})::int::text, 2, '0') || ':00' AS "Stunde",
              AVG(bucket_energy){powerMultiplier} AS "Ø Leistung"
            FROM (
              SELECT date_bin(INTERVAL '{bucketSize}', r.timestamp,
                TIMESTAMPTZ '2000-01-01') AS bucket, SUM(r.total_consumption) AS bucket_energy
              {scope} GROUP BY 1
            ) intervals GROUP BY 1 ORDER BY 1
            """, customerId, "kwatt", y + 13, 12));
        const string timeFilter = "$__timeFilter(r.timestamp)";
        if (!scope.Contains(timeFilter, StringComparison.Ordinal))
            throw new InvalidOperationException("The measurement scope is missing its time filter.");
        var previousScope = scope.Replace(timeFilter,
            "r.timestamp BETWEEN ($__timeFrom()::timestamptz - INTERVAL '7 days') " +
            "AND ($__timeTo()::timestamptz - INTERVAL '7 days')", StringComparison.Ordinal);
        var comparison = MeasurementPanel(panels.Count + 1,
            $"{label} · {(generation ? "Erzeugung" : "Verbrauch")} · Vergleich mit Vorwoche (-7 Tage)",
            $"""
            SELECT date_bin($__interval_ms * INTERVAL '1 millisecond',
              r.timestamp, TIMESTAMPTZ '2000-01-01') AS "time",
              SUM(r.total_consumption) AS "Aktuell"
            {scope} GROUP BY 1 ORDER BY 1
            """, customerId, "kwatth", y + 22);
        var previous = comparison["targets"]![0]!.DeepClone();
        previous["refId"] = "B";
        previous["rawSql"] = $"""
            SELECT date_bin($__interval_ms * INTERVAL '1 millisecond',
              r.timestamp + INTERVAL '7 days', TIMESTAMPTZ '2000-01-01') AS "time",
              SUM(r.total_consumption) AS "Vorwoche"
            {previousScope} GROUP BY 1 ORDER BY 1
            """;
        comparison["targets"]!.AsArray().Add(previous);
        panels.Add(comparison);
        if (!generation)
        {
            var hourlyColumns = string.Join(",\n", Enumerable.Range(0, 24).Select(hour =>
                $"SUM(energy) FILTER (WHERE EXTRACT(HOUR FROM bucket AT TIME ZONE 'Europe/Vienna') = {hour}) AS \"{hour}\""));
            var heatmap = MeasurementPanel(panels.Count + 1, $"{label} · Verbrauch nach Tag und Stunde (Wien)",
                $"""
                WITH hourly AS (
                  SELECT date_bin(INTERVAL '1 hour', r.timestamp,
                    TIMESTAMPTZ '2000-01-01') AS bucket, SUM(r.total_consumption) AS energy
                  {scope} GROUP BY 1
                )
                SELECT date_trunc('day', bucket, 'Europe/Vienna') AS "time",
                  {hourlyColumns}
                FROM hourly GROUP BY 1 ORDER BY 1
                """, customerId, "kwatth", y + 31);
            heatmap["type"] = "heatmap";
            heatmap["gridPos"]!["h"] = 12;
            heatmap["fieldConfig"]!["defaults"]!["unit"] = "none";
            heatmap["options"] = new JsonObject
            {
                ["calculate"] = false,
                ["mode"] = "heatmap",
                ["color"] = new JsonObject { ["mode"] = "scheme", ["scheme"] = "Oranges", ["steps"] = 64 },
                ["yAxis"] = new JsonObject { ["axisPlacement"] = "left", ["axisLabel"] = "Stunde (Wien)", ["unit"] = "none" },
                ["cellValues"] = new JsonObject { ["unit"] = "kwatth" }
            };
            panels.Add(heatmap);
        }
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
            ["colorMode"] = "value",
            ["reduceOptions"] = new JsonObject { ["calcs"] = new JsonArray("lastNotNull"),
                ["fields"] = "", ["values"] = false }
        };
        return panel;
    }

    private static JsonObject BarPanel(int id, string title, string sql, Guid customerId, string unit, int y, int x)
    {
        var panel = MeasurementPanel(id, title, sql, customerId, unit, y, x, 12);
        panel["type"] = "barchart";
        panel["targets"]![0]!["format"] = "table";
        return panel;
    }

    private static JsonObject MeasurementPanel(int id, string title, string sql, Guid customerId, string unit,
        int y, int x = 0, int width = 24)
    {
        var uid = ResourceIdentity.Dashboard(customerId, "datasource", customerId);
        return new JsonObject
        {
            ["id"] = id, ["title"] = title, ["type"] = "timeseries",
            ["gridPos"] = new JsonObject { ["h"] = 9, ["w"] = width, ["x"] = x, ["y"] = y },
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

    private static JsonObject Dashboard(CustomerSnapshot customer, string uid, string title, string content, JsonArray links) =>
        new()
        {
            ["uid"] = uid, ["title"] = title, ["schemaVersion"] = 39,
            ["editable"] = false,
            ["tags"] = new JsonArray(ResourceIdentity.OwnerTag(customer.Id)),
            ["links"] = links.DeepClone(),
            ["panels"] = content.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject
            {
                ["id"] = 1, ["type"] = "text", ["title"] = "Hinweis",
                ["gridPos"] = new JsonObject { ["h"] = 3, ["w"] = 24, ["x"] = 0, ["y"] = 0 },
                ["options"] = new JsonObject { ["mode"] = "markdown", ["content"] = Escape(content) }
            }),
            ["time"] = new JsonObject { ["from"] = "now-7d", ["to"] = "now" }
        };

    private static string Escape(string value) =>
        WebUtility.HtmlEncode(value).Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")
            .Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("#", "\\#");

    private sealed record OrganizationResult(long Id);
    private sealed record SearchResult(string Uid, List<string>? Tags = null);
    private sealed record NamedOrganization(long Id, string Name);
}
