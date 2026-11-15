using EGWNInterfaceGrafana.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EGWNInterfaceGrafana.Infrastructure;

public sealed class ServiceDbContext(DbContextOptions<ServiceDbContext> options) : DbContext(options)
{
    public DbSet<CustomerState> Customers => Set<CustomerState>();
    public DbSet<ProcessedEvent> Events => Set<ProcessedEvent>();
    public DbSet<UserMembership> Memberships => Set<UserMembership>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<CustomerState>().HasKey(c => c.Id);
        model.Entity<CustomerState>().Property(c => c.SnapshotJson).HasColumnType("jsonb");
        model.Entity<CustomerState>().HasIndex(c => c.CodeHash).IsUnique();
        model.Entity<ProcessedEvent>().HasKey(e => e.Id);
        model.Entity<ProcessedEvent>().HasIndex(e => e.CustomerId);
        model.Entity<UserMembership>().HasKey(m => m.UserId);
        model.Entity<UserMembership>().Property(m => m.UserId).HasMaxLength(128);
        model.Entity<UserMembership>().HasIndex(m => m.CustomerId);
        model.Entity<UserMembership>().HasOne<CustomerState>().WithMany().HasForeignKey(m => m.CustomerId);
    }

    public async Task<IDbContextTransaction> LockCustomerAsync(Guid id, CancellationToken ct)
    {
        var transaction = await Database.BeginTransactionAsync(ct);
        try
        {
            await Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString("N")}, 0))", ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
