using System.Text.Json;

namespace EGWNInterfaceGrafana.Api;

internal static class ConsulConfiguration
{
    public static async Task LoadAsync(ConfigurationManager configuration)
    {
        if (!configuration.GetValue<bool>("Consul:Enabled")) return;
        var address = configuration["Consul:Address"] ?? throw new InvalidOperationException("Consul address required.");
        var key = configuration["Consul:Key"] ?? throw new InvalidOperationException("Consul key required.");
        using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/kv/{Uri.EscapeDataString(key)}?raw");
        if (configuration["Consul:Token"] is { Length: > 0 } token) request.Headers.Add("X-Consul-Token", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await using var json = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(json);
        var values = new Dictionary<string, string?>();
        Flatten(document.RootElement, "", values);
        configuration.AddInMemoryCollection(values);
        // Deployment secrets and environment overrides take precedence over Consul.
        configuration.AddEnvironmentVariables();
    }

    private static void Flatten(JsonElement element, string prefix, IDictionary<string, string?> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var child in element.EnumerateObject())
                Flatten(child.Value, prefix.Length == 0 ? child.Name : prefix + ":" + child.Name, values);
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray()) Flatten(child, prefix + ":" + index++, values);
        }
        else values[prefix] = element.ValueKind == JsonValueKind.Null ? null : element.ToString();
    }
}
