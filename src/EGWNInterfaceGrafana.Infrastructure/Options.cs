namespace EGWNInterfaceGrafana.Infrastructure;

public sealed class RabbitOptions
{
    public bool Enabled { get; set; }
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseTls { get; set; }
    public string Exchange { get; set; } = "egwncentral.events";
    public string RoutingKey { get; set; } = "customer.created.v1";
    public string Queue { get; set; } = "grafana.customer-created.v1";
    public int MaxAttempts { get; set; } = 5;
    public int RetrySeconds { get; set; } = 2;
    public int MaxMessageBytes { get; set; } = 1_048_576;
}

public sealed class GrafanaOptions
{
    public string BaseUrl { get; set; } = "";
    public string AdminUser { get; set; } = "";
    public string AdminPassword { get; set; } = "";
}

public sealed class IdentityOptions
{
    public string BaseUrl { get; set; } = "";
    public string Realm { get; set; } = "egwn";
    public string ClientId { get; set; } = "egwn-provisioner";
    public string ClientSecret { get; set; } = "";
}

public sealed class RegistrationOptions
{
    public string InternalToken { get; set; } = "";
    public string HashKey { get; set; } = "";
}
