using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Domain;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed record TenantDataSource(string Schema, string User, string Password, string Host, int Port, string Database);

public sealed class TenantMeasurementProvisioner(HttpClient client, IOptions<MeasurementOptions> options)
{
    public bool Enabled => options.Value.Enabled;

    public async Task<TenantDataSource> ReconcileAsync(CustomerSnapshot customer, CancellationToken ct)
    {
        var communities = customer.EnergyCommunities ?? [];
        if (communities.Any(c => string.IsNullOrWhiteSpace(c.SourceId)) ||
            communities.Select(c => c.SourceId).Distinct(StringComparer.Ordinal).Count() != communities.Count)
            throw new InvalidEventException("Each energy community must have a unique Persistence sourceId.");
        var ownMeters = (customer.MeteringPoints ?? []).ToDictionary(m => m.Id);
        var meters = communities.SelectMany(c => c.MeteringPoints.Select(p =>
            new MeterEntitlement(c.SourceId!, p.MeteringPointCode, p.Id))).ToArray();
        if (meters.Any(p => !ownMeters.TryGetValue(p.Id, out var own) ||
                            own.MeteringPointCode != p.MeterPointNumber) ||
            ownMeters.Values.Any(p => p.EnergyDirection is not ("consumption" or "generation")))
            throw new InvalidEventException("Community meter points must belong to the customer snapshot.");

        var path = $"internal/grafana/customers/{customer.Id:D}";
        using var created = await SendAsync(HttpMethod.Post, $"{path}/credentials", null, HttpStatusCode.OK, ct);
        var credentials = await created.Content.ReadFromJsonAsync<Credentials>(EventPolicy.Json, ct);
        var expected = $"grafana_customer_{customer.Id:N}";
        if (credentials is null || credentials.Username != expected || credentials.Schema != expected ||
            string.IsNullOrWhiteSpace(credentials.Password))
            throw new InvalidOperationException("Persistence returned invalid customer credentials.");

        using var updated = await SendAsync(HttpMethod.Put, $"{path}/entitlements", new
        {
            communities = communities.Select(c => c.SourceId!).ToArray(),
            meters = meters.Select(m => new { communityId = m.CommunityId, meterPointNumber = m.MeterPointNumber }).ToArray()
        }, HttpStatusCode.NoContent, ct);
        var settings = options.Value;
        return new(credentials.Schema, credentials.Username, credentials.Password, settings.GrafanaDatabaseHost,
            settings.GrafanaDatabasePort, settings.GrafanaDatabaseName);
    }

    public async Task DeleteAsync(Guid customerId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post,
            $"internal/grafana/customers/{customerId:D}/disable", null, HttpStatusCode.NoContent, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, HttpStatusCode expected, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ServiceToken);
        if (body is not null) request.Content = JsonContent.Create(body, options: EventPolicy.Json);
        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == expected) return response;
        var status = response.StatusCode;
        response.Dispose();
        var target = request.RequestUri!.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        throw new HttpRequestException(
            $"Persistence provisioning request {method} {target} failed with HTTP {(int)status} ({status}); expected {(int)expected} ({expected}).",
            null, status);
    }

    private sealed record Credentials(string Username, string Schema, string Password);
    private sealed record MeterEntitlement(string CommunityId, string MeterPointNumber, Guid Id);
}
