using PayFlow.Contracts;
using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.Application;

public sealed record CreateOrderCommand(Guid CustomerId, decimal Amount, string Currency, string? IdempotencyKey);

public sealed record OrderDto(Guid Id, Guid CustomerId, decimal Amount, string Currency, string Status, DateTimeOffset CreatedAt);

public static class OrderMapping
{
    public static OrderDto ToDto(this Order o) =>
        new(o.Id, o.CustomerId, o.Amount, o.Currency, o.Status.ToString(), o.CreatedAt);
}

public sealed class CreateOrderHandler(
    IOrderRepository orders,
    IOutbox outbox,
    IUnitOfWork uow,
    ICorrelationContext correlation,
    TimeProvider clock)
{
    /// <returns>The order and whether it was newly created (false = idempotent replay).</returns>
    public async Task<(OrderDto Order, bool Created)> HandleAsync(CreateOrderCommand cmd, CancellationToken ct)
    {
        if (cmd.IdempotencyKey is not null &&
            await orders.GetByIdempotencyKeyAsync(cmd.IdempotencyKey, ct) is { } existing)
        {
            return (existing.ToDto(), false);
        }

        var now = clock.GetUtcNow();
        var order = Order.Create(cmd.CustomerId, cmd.Amount, cmd.Currency, now, cmd.IdempotencyKey);
        orders.Add(order);

        // Order + OutboxMessage are saved in ONE transaction (see OrderDbContext).
        outbox.Enqueue(
            new OrderCreated(Guid.NewGuid(), now, order.Id, order.CustomerId, order.Amount, order.Currency),
            correlation.CorrelationId);

        await uow.SaveChangesAsync(ct);
        return (order.ToDto(), true);
    }
}

public sealed class GetOrderHandler(IOrderRepository orders)
{
    public async Task<OrderDto?> HandleAsync(Guid id, CancellationToken ct) =>
        (await orders.GetAsync(id, ct))?.ToDto();
}

public enum CancelResult { Cancelled, NotFound, Conflict }

public sealed class CancelOrderHandler(IOrderRepository orders, IUnitOfWork uow, TimeProvider clock)
{
    public async Task<CancelResult> HandleAsync(Guid id, CancellationToken ct)
    {
        var order = await orders.GetAsync(id, ct);
        if (order is null) return CancelResult.NotFound;

        try { order.Cancel(clock.GetUtcNow()); }
        catch (DomainException) { return CancelResult.Conflict; }

        await uow.SaveChangesAsync(ct);
        return CancelResult.Cancelled;
    }
}
