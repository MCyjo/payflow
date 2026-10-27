namespace PayFlow.Contracts;

/// <summary>
/// Metadata carried by every message on the bus (as Service Bus properties, not in the body).
/// </summary>
public sealed record MessageMetadata(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    string? TraceParent);

public static class MessagingNames
{
    public const string PaymentCommandsQueue = "payment-commands";
    public const string DomainEventsTopic = "domain-events";

    public static class Subscriptions
    {
        public const string PaymentService = "payment-service";
        public const string OrderService = "order-service";
        public const string NotificationFunction = "notification-function";
        public const string Audit = "audit";
    }
}
