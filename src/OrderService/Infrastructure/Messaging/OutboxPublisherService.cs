using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PayFlow.OrderService.Infrastructure.Persistence;

namespace PayFlow.OrderService.Infrastructure.Messaging;

/// <summary>
/// Relays outbox rows to Service Bus. Delivery is at-least-once: a crash after send but before commit
/// re-publishes the row. That is safe because MessageId == EventId (broker duplicate detection)
/// and consumers are idempotent (inbox table).
/// </summary>
internal sealed partial class OutboxPublisherService(
    IServiceScopeFactory scopes,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(stoppingToken);
                if (published == 0) await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogBatchFailed(ex, ErrorDelay);
                await Task.Delay(ErrorDelay, stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // SKIP LOCKED lets several replicas drain the outbox concurrently without double-publishing.
        var batch = await db.OutboxMessages
            .FromSqlRaw($"""
                SELECT * FROM "OutboxMessages"
                WHERE "ProcessedAt" IS NULL
                ORDER BY "OccurredAt"
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        var published = 0;
        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(message, ct);
                message.ProcessedAt = DateTimeOffset.UtcNow;
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                LogPublishFailed(ex, message.Id, message.Attempts);
                break; // preserve ordering: stop at the first failure, retry next loop
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return published;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox batch failed; retrying in {Delay}")]
    private partial void LogBatchFailed(Exception ex, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to publish outbox message {MessageId} (attempt {Attempts})")]
    private partial void LogPublishFailed(Exception ex, Guid messageId, int attempts);
}
