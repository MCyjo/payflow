using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.IntegrationTests.Support;
using PayFlow.Messaging;
using PayFlow.OrderService.Application;
using PayFlow.OrderService.Domain;
using PayFlow.OrderService.Infrastructure.Persistence;

namespace PayFlow.IntegrationTests.Postgres;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class OrderPersistenceTests(PostgresFixture postgres)
{
    private static CreateOrderCommand Command(string? key = null) =>
        new(Guid.NewGuid(), 149.99m, "EUR", "tok_success", key);

    [Fact]
    public async Task CreateOrder_WritesOrderAndOutboxEventInTheSameTransaction()
    {
        await using var provider = await TestServices.OrderServicesAsync(await postgres.CreateDatabaseAsync("order_tx"));
        await using var scope = provider.CreateAsyncScope();

        var (order, _) = await scope.ServiceProvider.GetRequiredService<CreateOrderHandler>()
            .HandleAsync(Command(), CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.True(await db.Orders.AnyAsync(o => o.Id == order.Id));
        var outbox = await db.Set<OutboxMessage>().SingleAsync();
        Assert.Equal("OrderCreated", outbox.Type);
        Assert.Null(outbox.ProcessedAt);
        Assert.Contains(order.Id.ToString(), outbox.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParallelRequestsWithTheSameIdempotencyKey_CreateOneOrderAndOneEvent()
    {
        await using var provider = await TestServices.OrderServicesAsync(await postgres.CreateDatabaseAsync("order_idem"));
        var command = Command("same-key");

        // Without handling the unique-index race, losers would surface as HTTP 500s.
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<CreateOrderHandler>()
                .HandleAsync(command, CancellationToken.None);
        }));

        Assert.Single(results.Select(r => r.Order.Id).Distinct());
        Assert.Single(results, r => r.Created);

        await using var verify = provider.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync()); // the losers published nothing
    }

    [Fact]
    public async Task StaleWriter_LosesInsteadOfOverwritingAConcurrentChange()
    {
        await using var provider = await TestServices.OrderServicesAsync(await postgres.CreateDatabaseAsync("order_cc"));
        await using var seed = provider.CreateAsyncScope();
        var (created, _) = await seed.ServiceProvider.GetRequiredService<CreateOrderHandler>()
            .HandleAsync(Command(), CancellationToken.None);

        // Two requests load the same Pending order: the payment result confirms it, the customer cancels it.
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var db1 = first.ServiceProvider.GetRequiredService<OrderDbContext>();
        var db2 = second.ServiceProvider.GetRequiredService<OrderDbContext>();
        var confirmed = await db1.Orders.SingleAsync(o => o.Id == created.Id);
        var cancelled = await db2.Orders.SingleAsync(o => o.Id == created.Id);

        confirmed.Confirm(DateTimeOffset.UtcNow);
        await db1.SaveChangesAsync();
        cancelled.Cancel(DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        await using var verify = provider.CreateAsyncScope();
        var final = await verify.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.SingleAsync(o => o.Id == created.Id);
        Assert.Equal(OrderStatus.Confirmed, final.Status); // the first committed change stands
    }

    [Fact]
    public async Task CancelRacingWithConfirmation_IsReportedAsConflictNotAsAnError()
    {
        await using var provider = await TestServices.OrderServicesAsync(await postgres.CreateDatabaseAsync("order_cancel"));
        await using var seed = provider.CreateAsyncScope();
        var (created, _) = await seed.ServiceProvider.GetRequiredService<CreateOrderHandler>()
            .HandleAsync(Command(), CancellationToken.None);

        // The order is confirmed behind the cancel handler's back, after it loaded the order.
        await using var cancelScope = provider.CreateAsyncScope();
        var db = cancelScope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var loadedByCancel = await db.Orders.SingleAsync(o => o.Id == created.Id);
        await using (var other = provider.CreateAsyncScope())
        {
            var otherDb = other.ServiceProvider.GetRequiredService<OrderDbContext>();
            (await otherDb.Orders.SingleAsync(o => o.Id == created.Id)).Confirm(DateTimeOffset.UtcNow);
            await otherDb.SaveChangesAsync();
        }
        loadedByCancel.Cancel(DateTimeOffset.UtcNow);

        var uow = cancelScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => uow.SaveChangesAsync(CancellationToken.None));
    }
}
