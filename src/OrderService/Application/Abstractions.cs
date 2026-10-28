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

/// <summary>A unique index rejected the write (e.g. two concurrent requests with the same Idempotency-Key).</summary>
public sealed class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException() { }
    public UniqueConstraintViolationException(string message) : base(message) { }
    public UniqueConstraintViolationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The row changed since it was loaded (optimistic concurrency); reload and retry, or give up.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException() { }
    public ConcurrencyConflictException(string message) : base(message) { }
    public ConcurrencyConflictException(string message, Exception innerException) : base(message, innerException) { }
}
