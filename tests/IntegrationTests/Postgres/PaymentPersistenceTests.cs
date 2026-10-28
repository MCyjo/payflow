using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Contracts;
using PayFlow.IntegrationTests.Support;
using PayFlow.Messaging;
using PayFlow.PaymentService.Application;
using PayFlow.PaymentService.Domain;
using PayFlow.PaymentService.Infrastructure.Persistence;

namespace PayFlow.IntegrationTests.Postgres;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PaymentPersistenceTests(PostgresFixture postgres)
{
    /// <summary>
    /// Behaves like a real provider: charges are idempotent on the Idempotency-Key (the payment id),
    /// so it reports how many distinct charges it actually executed.
    /// </summary>
    private sealed class IdempotentProvider(ChargeOutcome outcome) : IPaymentProvider
    {
        private readonly ConcurrentDictionary<Guid, ChargeResult> _charges = new();
        private int _calls;

        public int Calls => _calls;
        public int DistinctCharges => _charges.Count;

        public Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_charges.GetOrAdd(request.PaymentId,
                _ => new ChargeResult(outcome, "prov_" + request.PaymentId.ToString("N")[..8], null)));
        }
    }

    private static OrderCreated NewOrder() =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), 100m, "EUR", "tok_success");

    private static async Task<int> CountAsync<T>(ServiceProvider provider, System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        where T : class
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Set<T>().CountAsync(predicate);
    }

    [Fact]
    public async Task ManyMessagesForTheSameOrder_ResultInOnePaymentOneChargeAndOneEvent()
    {
        var providerFake = new IdempotentProvider(ChargeOutcome.Succeeded);
        await using var provider = await TestServices.PaymentServicesAsync(
            await postgres.CreateDatabaseAsync("pay_dupes"), providerFake);
        var order = NewOrder();

        // Ten consumers receive ten differently-numbered messages for the same order at the same moment.
        async Task<Exception?> Deliver(int i)
        {
            try
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ProcessPaymentHandler>()
                    .HandleAsync(order, $"message-{i}", "corr", isLastAttempt: false, CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex; // the broker would abandon the message and redeliver it
            }
        }

        var firstPass = await Task.WhenAll(Enumerable.Range(0, 10).Select(Deliver));
        var failed = firstPass.Select((e, i) => (e, i)).Where(x => x.e is not null).Select(x => x.i).ToList();
        foreach (var i in failed) Assert.Null(await Deliver(i)); // the redelivery now succeeds

        Assert.Equal(1, await CountAsync<Payment>(provider, p => p.OrderId == order.OrderId));
        Assert.Equal(1, providerFake.DistinctCharges);           // the provider charged once, whatever the call count
        Assert.Equal(1, await CountAsync<OutboxMessage>(provider, m => m.Type == PaymentCompleted.Type));
        Assert.Equal(10, await CountAsync<ProcessedMessage>(provider, m => m.Consumer == "payment-service"));
    }

    [Fact]
    public async Task ManyDuplicateWebhooks_PublishPaymentCompletedOnce()
    {
        var providerFake = new IdempotentProvider(ChargeOutcome.Pending); // result will arrive by webhook
        await using var provider = await TestServices.PaymentServicesAsync(
            await postgres.CreateDatabaseAsync("pay_webhook"), providerFake);
        var order = NewOrder();
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProcessPaymentHandler>()
                .HandleAsync(order, "m1", "corr", false, CancellationToken.None);
        }
        var paymentId = await using_(provider, async db => (await db.Payments.SingleAsync()).Id);
        var webhook = new ProviderWebhook(paymentId, "completed", "prov_1", null);

        // The provider retried aggressively and 10 copies of the same webhook arrive in parallel.
        async Task<WebhookResult> Deliver()
        {
            await using var scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ProviderWebhookHandler>().HandleAsync(webhook, CancellationToken.None);
        }

        var first = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Deliver()));
        var retried = await Task.WhenAll(first.Where(r => r == WebhookResult.Conflict).Select(_ => Deliver()));

        Assert.Single(first, r => r == WebhookResult.Applied);
        Assert.All(retried, r => Assert.Equal(WebhookResult.Duplicate, r)); // the provider's retry sees the final state
        Assert.Equal(1, await CountAsync<OutboxMessage>(provider, m => m.Type == PaymentCompleted.Type));
        Assert.Equal(PaymentStatus.Completed, await using_(provider, async db => (await db.Payments.SingleAsync()).Status));
    }

    private static async Task<T> using_<T>(ServiceProvider provider, Func<PaymentDbContext, Task<T>> query)
    {
        await using var scope = provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<PaymentDbContext>());
    }
}
