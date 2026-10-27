using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PayFlow.Messaging;

/// <summary>Handles one message inside a DI scope. Throw to abandon (retry); throw <see cref="PermanentMessageException"/> to dead-letter.</summary>
public interface IMessageProcessor
{
    Task ProcessAsync(ServiceBusReceivedMessage message, CancellationToken ct);
}

/// <summary>The message can never succeed (malformed, unknown type): retrying is pointless, send to the DLQ.</summary>
public sealed class PermanentMessageException : Exception
{
    public PermanentMessageException() { }
    public PermanentMessageException(string message) : base(message) { }
    public PermanentMessageException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed partial class SubscriptionConsumerService<TProcessor>(
    ServiceBusClient client,
    IServiceScopeFactory scopes,
    ILogger<SubscriptionConsumerService<TProcessor>> logger,
    string topic,
    string subscription) : BackgroundService where TProcessor : IMessageProcessor
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = client.CreateProcessor(topic, subscription, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = true,   // complete on success, abandon (=> redelivery, then DLQ) on exception
            MaxConcurrentCalls = 4,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5)
        });

        processor.ProcessMessageAsync += HandleAsync;
        processor.ProcessErrorAsync += args =>
        {
            LogProcessorError(args.Exception, args.EntityPath, args.ErrorSource);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
        await processor.StopProcessingAsync(CancellationToken.None);
    }

    private async Task HandleAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;

        // Continue the sender's trace: this span is a child of the publisher's span.
        using var activity = MessagingTelemetry.Source.StartActivity(
            $"{subscription} process", ActivityKind.Consumer, MessagingTelemetry.ExtractParent(message));
        activity?.SetTag("messaging.system", "servicebus");
        activity?.SetTag("messaging.destination.name", $"{topic}/{subscription}");
        activity?.SetTag("messaging.message.id", message.MessageId);
        activity?.SetTag("messaging.message.delivery_count", message.DeliveryCount);
        activity?.SetTag("payflow.event_type", message.Subject);
        activity?.SetTag("payflow.correlation_id", message.CorrelationId);

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = message.MessageId,
            ["CorrelationId"] = message.CorrelationId ?? string.Empty
        });

        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<TProcessor>();
            await handler.ProcessAsync(message, args.CancellationToken);
        }
        catch (PermanentMessageException ex)
        {
            outcome = "dead_lettered";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogDeadLettered(ex, message.MessageId);
            await args.DeadLetterMessageAsync(message, deadLetterReason: "PermanentFailure",
                deadLetterErrorDescription: ex.Message, cancellationToken: args.CancellationToken);
        }
        catch (Exception ex)
        {
            outcome = "abandoned"; // the SDK abandons the message; the broker redelivers it, then dead-letters it
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            MessagingTelemetry.MessagesProcessed.Add(1,
                new KeyValuePair<string, object?>("subscription", subscription),
                new KeyValuePair<string, object?>("outcome", outcome));
            MessagingTelemetry.ProcessingDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("subscription", subscription));
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Service Bus processor error on {EntityPath} ({Source})")]
    private partial void LogProcessorError(Exception ex, string entityPath, ServiceBusErrorSource source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dead-lettering message {MessageId}")]
    private partial void LogDeadLettered(Exception ex, string messageId);
}
