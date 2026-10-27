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
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = message.MessageId,
            ["CorrelationId"] = message.CorrelationId ?? string.Empty
        });

        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<TProcessor>();
        try
        {
            await handler.ProcessAsync(message, args.CancellationToken);
        }
        catch (PermanentMessageException ex)
        {
            LogDeadLettered(ex, message.MessageId);
            await args.DeadLetterMessageAsync(message, deadLetterReason: "PermanentFailure",
                deadLetterErrorDescription: ex.Message, cancellationToken: args.CancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Service Bus processor error on {EntityPath} ({Source})")]
    private partial void LogProcessorError(Exception ex, string entityPath, ServiceBusErrorSource source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dead-lettering message {MessageId}")]
    private partial void LogDeadLettered(Exception ex, string messageId);
}
