using Microsoft.Extensions.Logging.Abstractions;
using PayFlow.Contracts;
using PayFlow.OrderService.Application;
using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.UnitTests;

public class ApplyPaymentResultHandlerTests
{
    private sealed class FakeStore : IOrderRepository, IInbox, IUnitOfWork
    {
        public List<Order> Orders { get; } = [];
        public HashSet<string> Inbox { get; } = [];
        public int Saves { get; private set; }

        public Task<Order?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Orders.FirstOrDefault(o => o.Id == id));
        public Task<Order?> GetByIdempotencyKeyAsync(string key, CancellationToken ct) =>
            Task.FromResult<Order?>(null);
        public void Add(Order order) => Orders.Add(order);
        public Task<bool> HasProcessedAsync(string messageId, CancellationToken ct) =>
            Task.FromResult(Inbox.Contains(messageId));
        public void MarkProcessed(string messageId) => Inbox.Add(messageId);
        public Task SaveChangesAsync(CancellationToken ct)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private static (FakeStore Store, Order Order, ApplyPaymentResultHandler Handler) Arrange()
    {
        var store = new FakeStore();
        var order = Order.Create(Guid.NewGuid(), 100m, "EUR", DateTimeOffset.UtcNow);
        store.Add(order);
        return (store, order, new ApplyPaymentResultHandler(
            store, store, store, TimeProvider.System, NullLogger<ApplyPaymentResultHandler>.Instance));
    }

    private static PaymentCompleted Completed(Order o) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, o.Id, Guid.NewGuid(), o.Amount, o.Currency);

    private static PaymentFailed Failed(Order o) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, o.Id, Guid.NewGuid(), "card_declined");

    [Fact]
    public async Task PaymentCompleted_ConfirmsOrder()
    {
        var (store, order, handler) = Arrange();

        await handler.HandleAsync(Completed(order), "m1", CancellationToken.None);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Contains("m1", store.Inbox);
    }

    [Fact]
    public async Task PaymentFailed_RejectsOrder()
    {
        var (_, order, handler) = Arrange();

        await handler.HandleAsync(Failed(order), "m1", CancellationToken.None);

        Assert.Equal(OrderStatus.Rejected, order.Status);
    }

    [Fact]
    public async Task RedeliveredMessage_IsSkipped()
    {
        var (store, order, handler) = Arrange();
        var evt = Completed(order);

        await handler.HandleAsync(evt, "m1", CancellationToken.None);
        await handler.HandleAsync(evt, "m1", CancellationToken.None);

        Assert.Equal(1, store.Saves);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public async Task PaymentCompletedForCancelledOrder_KeepsCancelledAndAcknowledgesMessage()
    {
        var (store, order, handler) = Arrange();
        order.Cancel(DateTimeOffset.UtcNow);

        await handler.HandleAsync(Completed(order), "m1", CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Contains("m1", store.Inbox); // not retried forever
    }

    [Fact]
    public async Task UnknownOrder_Throws_SoBrokerRetriesAndDeadLetters()
    {
        var (_, _, handler) = Arrange();
        var ghost = new PaymentCompleted(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), 1m, "EUR");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(ghost, "m1", CancellationToken.None));
    }
}
