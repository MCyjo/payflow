using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PayFlow.Contracts;

namespace PayFlow.NotificationFunction;

/// <summary>
/// Reacts to PaymentCompleted / PaymentFailed (the "notification-function" subscription filters to those two).
/// The email is only logged; the point is the event-driven wiring. Delivery is at-least-once, so a real sender
/// would pass the MessageId as an idempotency key to the email provider.
/// </summary>
public sealed partial class PaymentNotificationFunction(ILogger<PaymentNotificationFunction> logger)
{
    [Function(nameof(PaymentNotificationFunction))]
    public async Task Run(
        [ServiceBusTrigger(
            MessagingNames.DomainEventsTopic,
            MessagingNames.Subscriptions.NotificationFunction,
            Connection = "ServiceBus")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(actions);

        var notification = NotificationComposer.TryCompose(message.Subject, message.Body);
        if (notification is null)
        {
            LogUnsupported(message.MessageId, message.Subject);
            await actions.DeadLetterMessageAsync(message,
                deadLetterReason: "UnsupportedMessage",
                deadLetterErrorDescription: $"Cannot build a notification from '{message.Subject}'.",
                cancellationToken: ct);
            return;
        }

        LogEmail(notification.Subject, notification.OrderId, message.CorrelationId, notification.Body);
        // Returning normally lets the host complete the message.
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email: {Subject} | order {OrderId} | correlation {CorrelationId} | {Body}")]
    private partial void LogEmail(string subject, Guid orderId, string? correlationId, string body);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dead-lettering unsupported message {MessageId} ({Subject})")]
    private partial void LogUnsupported(string messageId, string? subject);
}

/// <summary>
/// Fan-out demo: the "audit" subscription has no filter and receives every domain event.
/// In Azure this would append to Table Storage / Cosmos DB; here it writes a structured audit line.
/// </summary>
public sealed partial class AuditFunction(ILogger<AuditFunction> logger)
{
    [Function(nameof(AuditFunction))]
    public void Run(
        [ServiceBusTrigger(
            MessagingNames.DomainEventsTopic,
            MessagingNames.Subscriptions.Audit,
            Connection = "ServiceBus")] ServiceBusReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogAudit(message.Subject, message.MessageId, message.CorrelationId, message.EnqueuedTime);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "AUDIT {EventType} | message {MessageId} | correlation {CorrelationId} | enqueued {EnqueuedAt:O}")]
    private partial void LogAudit(string? eventType, string messageId, string? correlationId, DateTimeOffset enqueuedAt);
}
