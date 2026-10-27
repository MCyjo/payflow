using PayFlow.Contracts;
using PayFlow.OrderService.Application;
using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.UnitTests;

public class CreateOrderHandlerTests
{
    private sealed class FakeStore : IOrderRepository, IOutbox, IUnitOfWork, ICorrelationContext
    {
        public List<Order> Orders { get; } = [];
        public List<(IDomainEvent Event, string CorrelationId)> Outbox { get; } = [];
        public int Saves { get; private set; }
        public string CorrelationId => "corr-1";

        public Task<Order?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Orders.FirstOrDefault(o => o.Id == id));

        public Task<Order?> GetByIdempotencyKeyAsync(string key, CancellationToken ct) =>
            Task.FromResult(Orders.FirstOrDefault(o => o.IdempotencyKey == key));

        public void Add(Order order) => Orders.Add(order);
        public void Enqueue(IDomainEvent domainEvent, string correlationId) => Outbox.Add((domainEvent, correlationId));

        public Task SaveChangesAsync(CancellationToken ct)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private static CreateOrderHandler Handler(FakeStore s) => new(s, s, s, s, TimeProvider.System);

    [Fact]
    public async Task Handle_SavesOrderAndOutboxEventInOneUnitOfWork()
    {
        var store = new FakeStore();
        var customer = Guid.NewGuid();

        var (dto, created) = await Handler(store).HandleAsync(
            new CreateOrderCommand(customer, 149.99m, "EUR", null), CancellationToken.None);

        Assert.True(created);
        Assert.Equal(1, store.Saves);
        var (evt, correlationId) = Assert.Single(store.Outbox);
        var orderCreated = Assert.IsType<OrderCreated>(evt);
        Assert.Equal(dto.Id, orderCreated.OrderId);
        Assert.Equal(149.99m, orderCreated.Amount);
        Assert.Equal("corr-1", correlationId);
    }

    [Fact]
    public async Task Handle_SameIdempotencyKey_ReturnsExistingOrderWithoutSecondEvent()
    {
        var store = new FakeStore();
        var cmd = new CreateOrderCommand(Guid.NewGuid(), 10m, "EUR", "key-1");

        var first = await Handler(store).HandleAsync(cmd, CancellationToken.None);
        var second = await Handler(store).HandleAsync(cmd, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Order.Id, second.Order.Id);
        Assert.Single(store.Orders);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task Handle_InvalidOrder_PersistsNothing()
    {
        var store = new FakeStore();

        await Assert.ThrowsAsync<DomainException>(() => Handler(store).HandleAsync(
            new CreateOrderCommand(Guid.NewGuid(), -1m, "EUR", null), CancellationToken.None));

        Assert.Equal(0, store.Saves);
        Assert.Empty(store.Outbox);
    }
}
