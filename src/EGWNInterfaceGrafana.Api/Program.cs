using System.Threading.RateLimiting;
using EGWNInterfaceGrafana.Api;
using EGWNInterfaceGrafana.Application;
using EGWNInterfaceGrafana.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16_384);
await ConsulConfiguration.LoadAsync(builder.Configuration);
builder.Services.AddSerilog((services, logging) => logging
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext().WriteTo.Console());

builder.Services.AddOptions<RabbitOptions>().BindConfiguration("RabbitMQ")
    .Validate(o => o.MaxAttempts is >= 1 and <= 20 && o.RetrySeconds is >= 1 and <= 60 &&
        o.MaxMessageBytes > 0 && (!o.Enabled ||
        (!string.IsNullOrWhiteSpace(o.HostName) && o.Port is > 0 and <= 65535 &&
         !string.IsNullOrWhiteSpace(o.UserName) && !string.IsNullOrWhiteSpace(o.Password) &&
         !string.IsNullOrWhiteSpace(o.Queue) && !string.IsNullOrWhiteSpace(o.Exchange) &&
         !string.IsNullOrWhiteSpace(o.RoutingKey))), "Invalid RabbitMQ settings.").ValidateOnStart();
builder.Services.AddOptions<GrafanaOptions>().BindConfiguration("Grafana")
    .Validate(o => ValidUrl(o.BaseUrl) && o.AdminUser.Length > 0 && o.AdminPassword.Length > 0,
        "Grafana URL and administrative credentials required.").ValidateOnStart();
builder.Services.AddOptions<MeasurementOptions>().BindConfiguration("Measurement")
    .Validate(o => !o.Enabled || (o.ProvisioningConnectionString.Length > 0 &&
        o.GrafanaDatabaseHost.Length > 0 && o.GrafanaDatabasePort is > 0 and <= 65535 &&
        o.CredentialKey.Length >= 32), "Measurement database connection, Grafana host and independent credential key required.")
    .ValidateOnStart();
builder.Services.AddOptions<IdentityOptions>().BindConfiguration("Keycloak")
    .Validate(o => ValidUrl(o.BaseUrl) && o.Realm.Length > 0 && o.ClientId.Length > 0 && o.ClientSecret.Length > 0,
        "Keycloak URL, realm and provisioning client credentials required.").ValidateOnStart();
builder.Services.AddOptions<RegistrationOptions>().BindConfiguration("Registration")
    .Validate(o => o.HashKey.Length >= 32 && o.InternalToken.Length >= 32 && o.HashKey != o.InternalToken,
        "Distinct registration hash key and internal token of at least 32 characters required.").ValidateOnStart();
var connectionString = builder.Configuration.GetConnectionString("Service")
    ?? throw new InvalidOperationException("ConnectionStrings:Service required.");
builder.Services.AddDbContext<ServiceDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RegistrationSecrets>();
builder.Services.AddSingleton<RabbitStatus>();
builder.Services.AddScoped<EventProcessor>();
builder.Services.AddScoped<RegistrationService>();
builder.Services.AddScoped<TenantMeasurementProvisioner>();
builder.Services.AddHttpClient<IGrafanaProvisioner, GrafanaProvisioner>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Grafana:BaseUrl"]!.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<IIdentityRevoker, KeycloakRevoker>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Keycloak:BaseUrl"]!.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHostedService<RabbitConsumer>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("registration", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();
var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ServiceDbContext>().Database.MigrateAsync();
    return;
}

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/internal"))
    {
        var secrets = context.RequestServices.GetRequiredService<RegistrationSecrets>();
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !secrets.Authenticate(authorization[7..]))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next(context);
});
app.UseRateLimiter();
app.MapHealthChecks("/health/live");
app.MapGet("/health/ready", async (ServiceDbContext db, RabbitStatus broker, CancellationToken ct) =>
{
    var enabled = builder.Configuration.GetValue<bool>("RabbitMQ:Enabled");
    return await db.Database.CanConnectAsync(ct) && (!enabled || broker.Connected)
        ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
});
var registration = app.MapGroup("/internal/registration").RequireRateLimiting("registration");
registration.MapPost("/resolve", async (ResolveRequest request, RegistrationService service, ILogger<Program> logger, CancellationToken ct) =>
    await RegistrationResult(async () =>
    {
        if (request.Code is null) throw new RegistrationDeniedException();
        return await service.ResolveAsync(request.Code, ct);
    }, logger));
registration.MapPost("/assign", async (AssignRequest request, RegistrationService service, ILogger<Program> logger, CancellationToken ct) =>
    await RegistrationResult(async () =>
    {
        if (request.Ticket is null || request.UserId is null) throw new RegistrationDeniedException();
        return await service.AssignAsync(request.Ticket, request.UserId, ct);
    }, logger));
registration.MapPost("/membership", async (MembershipRequest request, RegistrationService service, ILogger<Program> logger, CancellationToken ct) =>
    await RegistrationResult(async () =>
    {
        if (request.UserId is null) throw new RegistrationDeniedException();
        return await service.MembershipAsync(request.UserId, ct);
    }, logger));
await app.RunAsync();

static bool ValidUrl(string value) =>
    Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

static async Task<IResult> RegistrationResult<T>(Func<Task<T>> action, Microsoft.Extensions.Logging.ILogger logger)
{
    try { return Results.Ok(await action()); }
    catch (RegistrationDeniedException)
    {
        logger.LogWarning("Registration authorization denied");
        return Results.BadRequest(new { error = "Registration not permitted." });
    }
}

public sealed record ResolveRequest(string? Code);
public sealed record AssignRequest(string? Ticket, string? UserId);
public sealed record MembershipRequest(string? UserId);
public partial class Program;
