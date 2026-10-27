using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using PayFlow.Contracts;

namespace PayFlow.Messaging.UnitTests;

public sealed class TracePropagationTests : IDisposable
{
    private readonly ActivityListener _listener = new()
    {
        ShouldListenTo = source => source.Name == TelemetryNames.MessagingSource,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
    };

    public TracePropagationTests() => ActivitySource.AddActivityListener(_listener);

    public void Dispose() => _listener.Dispose();

    private static OutboxMessage Outbox(string? traceParent = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = "OrderCreated",
        Payload = "{}",
        CorrelationId = "corr-1",
        TraceParent = traceParent,
        OccurredAt = DateTimeOffset.UtcNow
    };

    private static ServiceBusReceivedMessage Receive(ServiceBusMessage sent) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: sent.Body,
            messageId: sent.MessageId,
            subject: sent.Subject,
            correlationId: sent.CorrelationId,
            properties: sent.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value));

    [Fact]
    public void BuildMessage_CopiesIdentityAndRoutingFields()
    {
        var outbox = Outbox();

        var message = ServiceBusEventPublisher.BuildMessage(outbox, publishActivity: null);

        Assert.Equal(outbox.Id.ToString(), message.MessageId);   // broker duplicate detection key
        Assert.Equal("OrderCreated", message.Subject);           // subscription filters match on this
        Assert.Equal("corr-1", message.CorrelationId);
    }

    [Fact]
    public void BuildMessage_WithoutActivity_FallsBackToTraceParentStoredInOutbox()
    {
        const string stored = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        var message = ServiceBusEventPublisher.BuildMessage(Outbox(stored), publishActivity: null);

        Assert.Equal(stored, message.ApplicationProperties[TelemetryNames.TraceParentProperty]);
        Assert.Equal(stored, message.ApplicationProperties[TelemetryNames.DiagnosticIdProperty]);
    }

    [Fact]
    public void ConsumerSpan_JoinsTheTraceOfThePublisherSpan()
    {
        using var publish = MessagingTelemetry.Source.StartActivity("publish", ActivityKind.Producer);
        Assert.NotNull(publish);

        var received = Receive(ServiceBusEventPublisher.BuildMessage(Outbox(), publish));
        using var consume = MessagingTelemetry.Source.StartActivity(
            "process", ActivityKind.Consumer, MessagingTelemetry.ExtractParent(received));

        Assert.NotNull(consume);
        Assert.Equal(publish.TraceId, consume.TraceId);
        Assert.Equal(publish.SpanId, consume.ParentSpanId);
    }

    [Fact]
    public void PublisherSpan_ContinuesTheTraceThatEnqueuedTheEvent()
    {
        // The relay runs later on a background thread, yet must stay in the original request's trace.
        using var request = MessagingTelemetry.Source.StartActivity("request");
        Assert.NotNull(request);
        var outbox = Outbox(request.Id);
        request.Stop();

        Assert.True(ActivityContext.TryParse(outbox.TraceParent, null, out var parent));
        using var publish = MessagingTelemetry.Source.StartActivity("publish", ActivityKind.Producer, parent);

        Assert.NotNull(publish);
        Assert.Equal(request.TraceId, publish.TraceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    public void ExtractParent_WithMissingOrInvalidHeader_ReturnsDefault(string? value)
    {
        var properties = new Dictionary<string, object>();
        if (value is not null) properties[TelemetryNames.TraceParentProperty] = value;
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(properties: properties);

        Assert.Equal(default, MessagingTelemetry.ExtractParent(received));
    }
}
