using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ServiceDbContext>
{
    public ServiceDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ServiceDbContext>().UseNpgsql(
            Environment.GetEnvironmentVariable("ConnectionStrings__Service") ??
            "Host=localhost;Database=egwn_grafana").Options);
}
