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
    HttpClient client, IOptions<GrafanaOptions> options, ServiceDbContext db) : IGrafanaProvisioner
{
    public async Task<long> ReconcileAsync(CustomerSnapshot customer, long? organizationId, CancellationToken ct)
    {
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

        var dashboards = BuildDashboards(customer);
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
        return orgId.Value;
    }

    public async Task DeleteAsync(Guid customerId, long? organizationId, CancellationToken ct)
    {
        var actual = await FindOrganizationAsync(customerId, ct);
        if (actual is null)
        {
            await SynchronizeOrgMappingAsync(ct);
            return;
        }
        if (organizationId is { } stored && stored != actual)
            throw new InvalidEventException("Refusing to delete an unexpected organization.");
        using var response = await SendAsync(HttpMethod.Delete, $"api/orgs/{actual}", null, null, ct, HttpStatusCode.NotFound);
        await SynchronizeOrgMappingAsync(ct);
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

    public static List<JsonObject> BuildDashboards(CustomerSnapshot customer)
    {
        var overviewUid = ResourceIdentity.Dashboard(customer.Id, "customer", customer.Id);
        var result = new List<JsonObject>();
        var points = customer.MeteringPoints ?? [];
        var communities = customer.EnergyCommunities ?? [];
        var summary = new StringBuilder();
        summary.AppendLine($"# {Escape(customer.Name)}").AppendLine()
            .AppendLine($"Customer: {Escape(customer.CustomerNumber)}").AppendLine()
            .AppendLine("Measurement queries are not connected yet.").AppendLine();
        foreach (var point in points)
        {
            var uid = ResourceIdentity.Dashboard(customer.Id, "meter", point.Id);
            summary.AppendLine($"- [{Escape(point.MeteringPointCode)}](/d/{uid})");
            result.Add(Dashboard(customer, uid, $"Meter - {point.MeteringPointCode}",
                $"{Escape(point.Description ?? point.MeteringPointCode)}\n\nDirection: {Escape(point.EnergyDirection)}\n\nMeasurement queries are not connected yet.",
                overviewUid));
        }
        foreach (var community in communities)
        {
            var uid = ResourceIdentity.Dashboard(customer.Id, "community", community.Id);
            summary.AppendLine($"- [{Escape(community.Name)}](/d/{uid})");
            var text = new StringBuilder($"# {Escape(community.Name)}\n\nMeasurement queries are not connected yet.\n\n");
            foreach (var point in community.MeteringPoints)
                text.AppendLine($"- {Escape(point.MeteringPointCode)}: {point.ParticipationPercentage.ToString(System.Globalization.CultureInfo.InvariantCulture)}%");
            result.Add(Dashboard(customer, uid, $"Community - {community.Name}", text.ToString(), overviewUid));
        }
        result.Add(Dashboard(customer, overviewUid, $"{customer.CustomerNumber} - {customer.Name}", summary.ToString(), overviewUid));
        return result;
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
