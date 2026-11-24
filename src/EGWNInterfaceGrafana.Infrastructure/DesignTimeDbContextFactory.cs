using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ServiceDbContext>
{
    public ServiceDbContext CreateDbContext(string[] args)
    {
        var current = Directory.GetCurrentDirectory();
        var apiSettings = new[]
        {
            Path.Combine(current, "appsettings.json"),
            Path.Combine(current, "..", "EGWNInterfaceGrafana.Api", "appsettings.json"),
            Path.Combine(current, "src", "EGWNInterfaceGrafana.Api", "appsettings.json")
        }.FirstOrDefault(File.Exists);
        var builder = new ConfigurationBuilder();
        if (apiSettings is not null) builder.AddJsonFile(apiSettings);
        var configuration = builder.AddUserSecrets("egwn-interface-grafana-api")
            .AddEnvironmentVariables().Build();
        var connectionString = configuration.GetConnectionString("Service");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Set ConnectionStrings:Service in API User Secrets or ConnectionStrings__Service in the environment before running migrations.");
        return new ServiceDbContext(new DbContextOptionsBuilder<ServiceDbContext>()
            .UseNpgsql(connectionString).Options);
    }
}
