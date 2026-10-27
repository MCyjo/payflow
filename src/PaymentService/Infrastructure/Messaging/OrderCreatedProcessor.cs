using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using PayFlow.Contracts;
using PayFlow.Messaging;
using PayFlow.PaymentService.Application;

namespace PayFlow.PaymentService.Infrastructure.Messaging;

internal sealed class OrderCreatedProcessor(ProcessPaymentHandler handler, IConfiguration config) : IMessageProcessor
{
    // Must match MaxDeliveryCount of the subscription (see docker/servicebus-config.json / Terraform).
    private readonly int _maxDeliveryCount = config.GetValue("ServiceBus:MaxDeliveryCount", 5);

    public async Task ProcessAsync(ServiceBusReceivedMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Subject != OrderCreated.Type)
            throw new PermanentMessageException($"Unexpected message type '{message.Subject}'.");

        OrderCreated? orderCreated;
        try
        {
            orderCreated = message.Body.ToObjectFromJson<OrderCreated>(JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            throw new PermanentMessageException("Malformed OrderCreated payload.", ex);
        }
        if (orderCreated is null) throw new PermanentMessageException("Empty OrderCreated payload.");

        await handler.HandleAsync(
            orderCreated,
            message.MessageId,
            message.CorrelationId ?? orderCreated.OrderId.ToString("N"),
            isLastAttempt: message.DeliveryCount >= _maxDeliveryCount,
            ct);
    }
}
