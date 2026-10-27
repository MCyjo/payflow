using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PayFlow.Contracts;

namespace PayFlow.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

internal sealed class ServiceBusEventPublisher(ServiceBusClient client) : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender _sender = client.CreateSender(MessagingNames.DomainEventsTopic);

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The span continues the trace of whoever enqueued the event (HTTP request or message handler),
        // however much later the relay runs.
        _ = ActivityContext.TryParse(message.TraceParent, null, out var parent);
        using var activity = MessagingTelemetry.Source.StartActivity(
            $"{MessagingNames.DomainEventsTopic} publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "servicebus");
        activity?.SetTag("messaging.destination.name", MessagingNames.DomainEventsTopic);
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag("payflow.event_type", message.Type);
        activity?.SetTag("payflow.correlation_id", message.CorrelationId);

        try
        {
            await _sender.SendMessageAsync(BuildMessage(message, activity), ct);
            MessagingTelemetry.OutboxPublished.Add(1, new KeyValuePair<string, object?>("event_type", message.Type));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            MessagingTelemetry.OutboxPublishFailures.Add(1, new KeyValuePair<string, object?>("event_type", message.Type));
            throw;
        }
    }

    internal static ServiceBusMessage BuildMessage(OutboxMessage message, Activity? publishActivity)
    {
        var msg = new ServiceBusMessage(message.Payload)
        {
            MessageId = message.Id.ToString(),      // enables broker-side duplicate detection
            Subject = message.Type,                 // subscription filters match on this
            CorrelationId = message.CorrelationId,
            ContentType = "application/json"
        };
        msg.ApplicationProperties["eventType"] = message.Type;
        msg.ApplicationProperties["occurredAt"] = message.OccurredAt.ToString("O");

        var traceParent = publishActivity?.Id ?? message.TraceParent;
        if (traceParent is not null)
        {
            msg.ApplicationProperties[TelemetryNames.TraceParentProperty] = traceParent;
            msg.ApplicationProperties[TelemetryNames.DiagnosticIdProperty] = traceParent;
        }
        return msg;
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}

internal sealed class EfOutbox<TContext>(TContext db) : IOutbox where TContext : DbContext
{
    public void Enqueue(IDomainEvent domainEvent, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = domainEvent.EventId,
            Type = domainEvent.EventType,
            Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonSerializerOptions.Web),
            CorrelationId = correlationId,
            TraceParent = Activity.Current?.Id,
            OccurredAt = domainEvent.OccurredAt
        });
    }
}

internal sealed class EfInbox<TContext>(TContext db, TimeProvider clock, string consumer) : IInbox where TContext : DbContext
{
    public Task<bool> HasProcessedAsync(string messageId, CancellationToken ct) =>
        db.Set<ProcessedMessage>().AnyAsync(m => m.MessageId == messageId && m.Consumer == consumer, ct);

    public void MarkProcessed(string messageId) =>
        db.Set<ProcessedMessage>().Add(new ProcessedMessage
        {
            MessageId = messageId,
            Consumer = consumer,
            ProcessedAt = clock.GetUtcNow()
        });
}

/// <summary>
/// Relays outbox rows to Service Bus. Delivery is at-least-once: a crash after send but before commit
/// re-publishes the row. That is safe because MessageId == EventId (broker duplicate detection)
/// and consumers are idempotent (inbox table).
/// </summary>
internal sealed partial class OutboxPublisherService<TContext>(
    IServiceScopeFactory scopes,
    ILogger<OutboxPublisherService<TContext>> logger) : BackgroundService where TContext : DbContext
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
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // SKIP LOCKED lets several replicas drain the outbox concurrently without double-publishing.
        var batch = await db.Set<OutboxMessage>()
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

        // Deliberately not cancellable: messages already sent must be marked as processed even while the host is
        // shutting down, otherwise a graceful stop would needlessly re-publish them.
        await db.SaveChangesAsync(CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
        return published;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox batch failed; retrying in {Delay}")]
    private partial void LogBatchFailed(Exception ex, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to publish outbox message {MessageId} (attempt {Attempts})")]
    private partial void LogPublishFailed(Exception ex, Guid messageId, int attempts);
}
