using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayFlow.OrderService.Application;
using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.Infrastructure.Persistence;

internal sealed class OrderRepository(OrderDbContext db) : IOrderRepository
{
    public Task<Order?> GetAsync(Guid id, CancellationToken ct) =>
        db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Order?> GetByIdempotencyKeyAsync(string key, CancellationToken ct) =>
        db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct);

    public void Add(Order order) => db.Orders.Add(order);
}

/// <summary>SaveChanges is a single DB transaction: Order and OutboxMessage commit together or not at all.</summary>
internal sealed class EfUnitOfWork(OrderDbContext db) : IUnitOfWork
{
    private const string UniqueViolation = "23505";

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The row was modified concurrently.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw new UniqueConstraintViolationException("A unique constraint was violated.", ex);
        }
    }
}
