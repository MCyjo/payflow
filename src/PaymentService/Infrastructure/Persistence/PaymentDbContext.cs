using Microsoft.EntityFrameworkCore;
using PayFlow.Messaging;
using PayFlow.PaymentService.Application;
using PayFlow.PaymentService.Domain;

namespace PayFlow.PaymentService.Infrastructure.Persistence;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(e =>
        {
            e.ToTable("Payments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.Property(x => x.ProviderReference).HasMaxLength(100);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.HasIndex(x => x.OrderId).IsUnique();   // one payment per order, enforced by the database
        });

        modelBuilder.AddMessagingTables();
    }
}

internal sealed class PaymentRepository(PaymentDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Payment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct) =>
        db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, ct);

    public void Add(Payment payment) => db.Payments.Add(payment);
}


/// <summary>SaveChanges is one DB transaction: payment state, inbox record and outbox event commit together.</summary>
internal sealed class EfUnitOfWork(PaymentDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
