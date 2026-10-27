using PayFlow.Contracts;
using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.Application;

public interface IOrderRepository
{
    Task<Order?> GetAsync(Guid id, CancellationToken ct);
    Task<Order?> GetByIdempotencyKeyAsync(string key, CancellationToken ct);
    void Add(Order order);
}

/// <summary>Stages an event in the outbox; it is persisted by the same SaveChanges as the aggregate.</summary>
public interface IOutbox
{
    void Enqueue(IDomainEvent domainEvent, string correlationId);
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}

public interface ICorrelationContext
{
    string CorrelationId { get; }
}
