using Microsoft.Extensions.Logging.Abstractions;
using PayFlow.Contracts;
using PayFlow.PaymentService.Application;
using PayFlow.PaymentService.Domain;

namespace PayFlow.PaymentService.UnitTests;

public class ProcessPaymentHandlerTests
{
    private sealed class FakeProvider : IPaymentProvider
    {
        public Func<ChargeRequest, ChargeResult> OnCharge { get; set; } =
            r => new ChargeResult(ChargeOutcome.Succeeded, "prov_1", null);
        public List<ChargeRequest> Calls { get; } = [];

        public Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            return Task.FromResult(OnCharge(request));
        }
    }

    private sealed class FakeStore : IPaymentRepository, IInbox, IOutbox, IUnitOfWork
    {
        public List<Payment> Payments { get; } = [];
        public HashSet<string> Inbox { get; } = [];
        public List<IDomainEvent> Outbox { get; } = [];

        public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Payments.FirstOrDefault(p => p.Id == id));
        public Task<Payment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct) =>
            Task.FromResult(Payments.FirstOrDefault(p => p.OrderId == orderId));
        public void Add(Payment payment) => Payments.Add(payment);
        public Task<bool> HasProcessedAsync(string messageId, CancellationToken ct) =>
            Task.FromResult(Inbox.Contains(messageId));
        public void MarkProcessed(string messageId) => Inbox.Add(messageId);
        public void Enqueue(IDomainEvent domainEvent, string correlationId) => Outbox.Add(domainEvent);
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static OrderCreated NewOrder(string token = "tok_success") =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), 100m, "EUR", token);

    private static ProcessPaymentHandler Handler(FakeStore s, FakeProvider p) =>
        new(s, s, p, s, s, TimeProvider.System, NullLogger<ProcessPaymentHandler>.Instance);

    [Fact]
    public async Task Success_CompletesPaymentAndPublishesPaymentCompleted()
    {
        var store = new FakeStore();
        var order = NewOrder();

        await Handler(store, new FakeProvider()).HandleAsync(order, "m1", "c1", false, CancellationToken.None);

        var payment = Assert.Single(store.Payments);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        var evt = Assert.IsType<PaymentCompleted>(Assert.Single(store.Outbox));
        Assert.Equal(order.OrderId, evt.OrderId);
        Assert.Contains("m1", store.Inbox);
    }

    [Fact]
    public async Task Declined_FailsPaymentAndPublishesPaymentFailed()
    {
        var store = new FakeStore();
        var provider = new FakeProvider { OnCharge = _ => new ChargeResult(ChargeOutcome.Declined, null, "card_declined") };

        await Handler(store, provider).HandleAsync(NewOrder(), "m1", "c1", false, CancellationToken.None);

        Assert.Equal(PaymentStatus.Failed, Assert.Single(store.Payments).Status);
        var evt = Assert.IsType<PaymentFailed>(Assert.Single(store.Outbox));
        Assert.Equal("card_declined", evt.Reason);
    }

    [Fact]
    public async Task SameMessageTwice_ChargesOnlyOnce()
    {
        var store = new FakeStore();
        var provider = new FakeProvider();
        var order = NewOrder();
        var handler = Handler(store, provider);

        await handler.HandleAsync(order, "m1", "c1", false, CancellationToken.None);
        await handler.HandleAsync(order, "m1", "c1", false, CancellationToken.None); // redelivery

        Assert.Single(provider.Calls);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task SameOrderInDifferentMessage_ChargesOnlyOnce()
    {
        // e.g. the publisher re-sent the event after a crash, so it carries a new MessageId on the wire.
        var store = new FakeStore();
        var provider = new FakeProvider();
        var order = NewOrder();
        var handler = Handler(store, provider);

        await handler.HandleAsync(order, "m1", "c1", false, CancellationToken.None);
        await handler.HandleAsync(order, "m2", "c1", false, CancellationToken.None);

        Assert.Single(provider.Calls);
        Assert.Single(store.Payments);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task ProviderUnavailable_PropagatesSoBrokerRetries_AndLeavesPaymentProcessing()
    {
        var store = new FakeStore();
        var provider = new FakeProvider { OnCharge = _ => throw new ProviderUnavailableException("down") };

        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Handler(store, provider).HandleAsync(NewOrder(), "m1", "c1", false, CancellationToken.None));

        Assert.Equal(PaymentStatus.Processing, Assert.Single(store.Payments).Status);
        Assert.Empty(store.Outbox);
        Assert.DoesNotContain("m1", store.Inbox);
    }

    [Fact]
    public async Task RetryAfterCrashMidCharge_ReusesPaymentAndSameProviderIdempotencyKey()
    {
        var store = new FakeStore();
        var provider = new FakeProvider { OnCharge = _ => throw new ProviderUnavailableException("down") };
        var order = NewOrder();
        var handler = Handler(store, provider);

        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            handler.HandleAsync(order, "m1", "c1", false, CancellationToken.None));
        provider.OnCharge = _ => new ChargeResult(ChargeOutcome.Succeeded, "prov_1", null);
        await handler.HandleAsync(order, "m1", "c1", false, CancellationToken.None); // redelivery

        Assert.Single(store.Payments);
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(provider.Calls[0].PaymentId, provider.Calls[1].PaymentId); // provider dedupes on this
        Assert.Equal(PaymentStatus.Completed, store.Payments[0].Status);
    }

    [Fact]
    public async Task ProviderUnavailableOnLastAttempt_FailsPaymentInsteadOfStrandingOrder()
    {
        var store = new FakeStore();
        var provider = new FakeProvider { OnCharge = _ => throw new ProviderUnavailableException("down") };

        await Handler(store, provider).HandleAsync(NewOrder(), "m1", "c1", true, CancellationToken.None);

        Assert.Equal(PaymentStatus.Failed, Assert.Single(store.Payments).Status);
        Assert.IsType<PaymentFailed>(Assert.Single(store.Outbox));
    }

    [Fact]
    public async Task PendingFromProvider_StaysProcessingWithoutEvent()
    {
        var store = new FakeStore();
        var provider = new FakeProvider { OnCharge = _ => new ChargeResult(ChargeOutcome.Pending, "prov_9", null) };

        await Handler(store, provider).HandleAsync(NewOrder("tok_async"), "m1", "c1", false, CancellationToken.None);

        var payment = Assert.Single(store.Payments);
        Assert.Equal(PaymentStatus.Processing, payment.Status);
        Assert.Equal("prov_9", payment.ProviderReference);
        Assert.Empty(store.Outbox);
    }
}
