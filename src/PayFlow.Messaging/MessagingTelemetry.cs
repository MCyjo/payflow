using System.Diagnostics;
using System.Diagnostics.Metrics;
using Azure.Messaging.ServiceBus;
using PayFlow.Contracts;

namespace PayFlow.Messaging;

internal static class MessagingTelemetry
{
    public static readonly ActivitySource Source = new(TelemetryNames.MessagingSource);

    private static readonly Meter Meter = new(TelemetryNames.MessagingMeter);

    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("payflow.outbox.published", description: "Outbox messages sent to Service Bus.");

    public static readonly Counter<long> OutboxPublishFailures =
        Meter.CreateCounter<long>("payflow.outbox.publish_failures", description: "Failed attempts to send an outbox message.");

    /// <summary>Tagged with subscription and outcome: success, abandoned or dead_lettered.</summary>
    public static readonly Counter<long> MessagesProcessed =
        Meter.CreateCounter<long>("payflow.messages.processed", description: "Messages handled by a subscription consumer.");

    public static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>("payflow.messages.processing_duration", unit: "ms", description: "Time spent handling one message.");

    /// <summary>Reads the sender's trace context from a received message, if it carries a valid one.</summary>
    public static ActivityContext ExtractParent(ServiceBusReceivedMessage message)
    {
        if (message.ApplicationProperties.TryGetValue(TelemetryNames.TraceParentProperty, out var value)
            && value is string traceParent
            && ActivityContext.TryParse(traceParent, null, out var context))
        {
            return context;
        }
        return default;
    }
}
