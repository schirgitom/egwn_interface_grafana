using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EGWNInterfaceGrafana.Application;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed class KeycloakRevoker(HttpClient client, IOptions<IdentityOptions> options) : IIdentityRevoker
{
    public async Task RevokeAsync(string userId, CancellationToken ct)
    {
        var settings = options.Value;
        var realm = Uri.EscapeDataString(settings.Realm);
        using var tokenResponse = await client.PostAsync($"realms/{realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret
            }), ct);
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenDocument = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var token = tokenDocument.RootElement.GetProperty("access_token").GetString();
        var path = $"admin/realms/{realm}/users/{Uri.EscapeDataString(userId)}";
        using var get = new HttpRequestMessage(HttpMethod.Get, path);
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var userResponse = await client.SendAsync(get, ct);
        if (userResponse.StatusCode == HttpStatusCode.NotFound) return;
        userResponse.EnsureSuccessStatusCode();
        var user = await userResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? throw new HttpRequestException("Keycloak returned an empty user.");
        if (user["attributes"] is JsonObject attributes) attributes.Remove("egwn_org");
        using var update = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(user) };
        update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var updateResponse = await client.SendAsync(update, ct);
        updateResponse.EnsureSuccessStatusCode();
        using var logout = new HttpRequestMessage(HttpMethod.Post, path + "/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var logoutResponse = await client.SendAsync(logout, ct);
        logoutResponse.EnsureSuccessStatusCode();
    }
}
