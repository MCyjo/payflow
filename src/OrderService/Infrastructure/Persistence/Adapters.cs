using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayFlow.Contracts;
using PayFlow.OrderService.Application;
using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.Infrastructure.Persistence;

internal sealed class OrderRepository(OrderDbContext db) : IOrderRepository
{
    public Task<Order?> GetAsync(Guid id, CancellationToken ct) =>
        db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Order?> GetByIdempotencyKeyAsync(string key, CancellationToken ct) =>
        db.Orders.FirstOrDefaultAsync(o => o.IdempotencyKey == key, ct);

    public void Add(Order order) => db.Orders.Add(order);
}

internal sealed class EfOutbox(OrderDbContext db) : IOutbox
{
    public void Enqueue(IDomainEvent domainEvent, string correlationId) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = domainEvent.EventId,
            Type = domainEvent.EventType,
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonSerializerOptions.Web),
            CorrelationId = correlationId,
            TraceParent = Activity.Current?.Id,
            OccurredAt = domainEvent.OccurredAt
        });
}

/// <summary>SaveChanges is a single DB transaction: Order and OutboxMessage commit together or not at all.</summary>
internal sealed class EfUnitOfWork(OrderDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
