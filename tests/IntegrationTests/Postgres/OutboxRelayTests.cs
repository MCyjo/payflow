using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.IntegrationTests.Support;
using PayFlow.Messaging;
using PayFlow.OrderService.Infrastructure.Persistence;

namespace PayFlow.IntegrationTests.Postgres;

/// <summary>The outbox relay against a real PostgreSQL, with the broker replaced by a recording fake.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class OutboxRelayTests(PostgresFixture postgres)
{
    private static async Task SeedAsync(ServiceProvider provider, int count)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "OrderCreated",
                Payload = "{}",
                CorrelationId = "seed",
                OccurredAt = DateTimeOffset.UtcNow.AddMilliseconds(i)
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<int> PendingAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderDbContext>()
            .Set<OutboxMessage>().CountAsync(m => m.ProcessedAt == null);
    }

    [Fact]
    public async Task PublishesPendingMessagesAndMarksThemProcessed()
    {
        var cs = await postgres.CreateDatabaseAsync("relay_basic");
        await using var provider = await TestServices.OrderServicesAsync(cs);
        await SeedAsync(provider, 5);
        var publisher = new RecordingPublisher();

        using var relay = await TestServices.OrderRelayAsync(cs, publisher);
        await Poll.UntilAsync(async () => await PendingAsync(provider) == 0, "the outbox to drain", 30);
        await relay.StopAsync();

        Assert.Equal(5, publisher.Published.Count);
    }

    [Fact]
    public async Task FailedPublish_KeepsTheMessageRecordsTheErrorAndRetries()
    {
        var cs = await postgres.CreateDatabaseAsync("relay_retry");
        await using var provider = await TestServices.OrderServicesAsync(cs);
        await SeedAsync(provider, 1);
        var publisher = new RecordingPublisher
        {
            // Service Bus unavailable for the first two attempts.
            Behaviour = (_, attempt) => attempt <= 2 ? throw new InvalidOperationException("broker unavailable") : Task.CompletedTask
        };

        using var relay = await TestServices.OrderRelayAsync(cs, publisher);
        await Poll.UntilAsync(async () => await PendingAsync(provider) == 0, "the message to be published after retries", 30);
        await relay.StopAsync();

        await using var scope = provider.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Set<OutboxMessage>().SingleAsync();
        Assert.Equal(2, row.Attempts);                       // failures are counted ...
        Assert.Equal("broker unavailable", row.LastError);   // ... and visible to operators
        Assert.Single(publisher.Published);                  // nothing was lost, nothing duplicated
    }

    [Fact]
    public async Task CrashAfterSendBeforeCommit_RepublishesTheSameMessageId()
    {
        var cs = await postgres.CreateDatabaseAsync("relay_crash");
        await using var provider = await TestServices.OrderServicesAsync(cs);
        await SeedAsync(provider, 1);

        // "Sent to the broker, then the process died before the row was marked": the broker received the message,
        // but the relay saw a failure, so it cannot know the message went out and sends it again (at-least-once).
        var crashing = new CrashAfterSendPublisher(crashOnFirstSend: true);

        using var relay = await TestServices.OrderRelayAsync(cs, crashing);
        await Poll.UntilAsync(async () => await PendingAsync(provider) == 0, "the message to be re-published", 30);
        await relay.StopAsync();

        // Same MessageId both times: the topic's duplicate detection (and consumers' inbox) can collapse it.
        Assert.Equal(2, crashing.Sent.Count);
        Assert.Single(crashing.Sent.Distinct());
    }

    [Fact]
    public async Task TwoRelaysRunningTogether_PublishEveryMessageExactlyOnce()
    {
        var cs = await postgres.CreateDatabaseAsync("relay_skiplocked");
        await using var provider = await TestServices.OrderServicesAsync(cs);
        await SeedAsync(provider, 100);
        var publisher = new RecordingPublisher { Latency = TimeSpan.FromMilliseconds(5) };

        // Two replicas of the service draining the same table: FOR UPDATE SKIP LOCKED keeps them apart.
        using var relayA = await TestServices.OrderRelayAsync(cs, publisher);
        using var relayB = await TestServices.OrderRelayAsync(cs, publisher);
        await Poll.UntilAsync(async () => await PendingAsync(provider) == 0, "both relays to drain the outbox", 60);
        await relayA.StopAsync();
        await relayB.StopAsync();

        Assert.Equal(100, publisher.Published.Count);
        Assert.Equal(100, publisher.Published.Distinct().Count());
    }

    /// <summary>Records every send, then throws on the first one, simulating a crash before the commit.</summary>
    private sealed class CrashAfterSendPublisher(bool crashOnFirstSend) : IEventPublisher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _sent = new();
        private int _crashes = crashOnFirstSend ? 1 : 0;

        public System.Collections.Concurrent.ConcurrentQueue<Guid> Sent => _sent;

        public Task PublishAsync(OutboxMessage message, CancellationToken ct)
        {
            _sent.Enqueue(message.Id);                       // the broker received it
            if (Interlocked.Decrement(ref _crashes) >= 0) throw new InvalidOperationException("process died before commit");
            return Task.CompletedTask;
        }
    }
}
