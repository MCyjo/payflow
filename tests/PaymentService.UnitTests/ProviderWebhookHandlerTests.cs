using PayFlow.Contracts;
using PayFlow.PaymentService.Application;
using PayFlow.PaymentService.Domain;

namespace PayFlow.PaymentService.UnitTests;

public class ProviderWebhookHandlerTests
{
    private sealed class FakeStore : IPaymentRepository, IOutbox, IUnitOfWork
    {
        public List<Payment> Payments { get; } = [];
        public List<IDomainEvent> Outbox { get; } = [];
        public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(Payments.FirstOrDefault(p => p.Id == id));
        public Task<Payment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct) =>
            Task.FromResult(Payments.FirstOrDefault(p => p.OrderId == orderId));
        public void Add(Payment payment) => Payments.Add(payment);
        public void Enqueue(IDomainEvent domainEvent, string correlationId) => Outbox.Add(domainEvent);
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static (FakeStore Store, Payment Payment, ProviderWebhookHandler Handler) Arrange()
    {
        var store = new FakeStore();
        var payment = Payment.Create(Guid.NewGuid(), 50m, "EUR", "c1", DateTimeOffset.UtcNow);
        payment.StartProcessing(DateTimeOffset.UtcNow);
        store.Payments.Add(payment);
        return (store, payment, new ProviderWebhookHandler(store, store, store, TimeProvider.System));
    }

    [Fact]
    public async Task Completed_CompletesPaymentAndPublishesOnce()
    {
        var (store, payment, handler) = Arrange();

        var result = await handler.HandleAsync(new ProviderWebhook(payment.Id, "completed", "prov_1", null), CancellationToken.None);

        Assert.Equal(WebhookResult.Applied, result);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.IsType<PaymentCompleted>(Assert.Single(store.Outbox));
    }

    [Fact]
    public async Task DuplicateWebhook_IsAcknowledgedWithoutSecondEvent()
    {
        var (store, payment, handler) = Arrange();
        var hook = new ProviderWebhook(payment.Id, "completed", "prov_1", null);

        await handler.HandleAsync(hook, CancellationToken.None);
        var second = await handler.HandleAsync(hook, CancellationToken.None);

        Assert.Equal(WebhookResult.Duplicate, second);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task Failed_FailsPaymentAndPublishesPaymentFailed()
    {
        var (store, payment, handler) = Arrange();

        await handler.HandleAsync(new ProviderWebhook(payment.Id, "failed", null, "card_declined"), CancellationToken.None);

        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.IsType<PaymentFailed>(Assert.Single(store.Outbox));
    }

    [Fact]
    public async Task ConflictingWebhookAfterCompletion_IsRejected()
    {
        var (store, payment, handler) = Arrange();
        await handler.HandleAsync(new ProviderWebhook(payment.Id, "completed", "prov_1", null), CancellationToken.None);

        var result = await handler.HandleAsync(new ProviderWebhook(payment.Id, "failed", null, "late"), CancellationToken.None);

        Assert.Equal(WebhookResult.Rejected, result);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Single(store.Outbox);
    }

    [Fact]
    public async Task UnknownPayment_ReturnsNotFound()
    {
        var (_, _, handler) = Arrange();

        var result = await handler.HandleAsync(new ProviderWebhook(Guid.NewGuid(), "completed", null, null), CancellationToken.None);

        Assert.Equal(WebhookResult.NotFound, result);
    }
}
