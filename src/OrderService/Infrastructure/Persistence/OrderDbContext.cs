using Microsoft.EntityFrameworkCore;
using PayFlow.Messaging;
using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.Infrastructure.Persistence;

public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(e =>
        {
            e.ToTable("Orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();   // NULLs are distinct in PostgreSQL
            e.HasIndex(x => x.CustomerId);
        });

        modelBuilder.AddMessagingTables();
    }
}
