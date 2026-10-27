using PayFlow.Contracts;
using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.Application;

public interface IOrderRepository
{
    Task<Order?> GetAsync(Guid id, CancellationToken ct);
    Task<Order?> GetByIdempotencyKeyAsync(string key, CancellationToken ct);
    void Add(Order order);
}


public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}

public interface ICorrelationContext
{
    string CorrelationId { get; }
}
