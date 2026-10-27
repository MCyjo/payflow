using Azure.Messaging.ServiceBus;
using PayFlow.Contracts;
using PayFlow.OrderService.Infrastructure.Persistence;

namespace PayFlow.OrderService.Infrastructure.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

internal sealed class ServiceBusEventPublisher(ServiceBusClient client) : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusSender _sender = client.CreateSender(MessagingNames.DomainEventsTopic);

    public Task PublishAsync(OutboxMessage m, CancellationToken ct)
    {
        var msg = new ServiceBusMessage(m.Payload)
        {
            MessageId = m.Id.ToString(),            // enables broker-side duplicate detection
            Subject = m.Type,                       // subscription filters match on this
            CorrelationId = m.CorrelationId,
            ContentType = "application/json"
        };
        msg.ApplicationProperties["eventType"] = m.Type;
        msg.ApplicationProperties["occurredAt"] = m.OccurredAt.ToString("O");
        if (m.TraceParent is not null) msg.ApplicationProperties["traceparent"] = m.TraceParent;

        return _sender.SendMessageAsync(msg, ct);
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}
