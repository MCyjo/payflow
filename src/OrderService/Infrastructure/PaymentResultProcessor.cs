using System.Text.Json;
using Azure.Messaging.ServiceBus;
using PayFlow.Contracts;
using PayFlow.Messaging;
using PayFlow.OrderService.Application;

namespace PayFlow.OrderService.Infrastructure;

internal sealed class PaymentResultProcessor(ApplyPaymentResultHandler handler) : IMessageProcessor
{
    public async Task ProcessAsync(ServiceBusReceivedMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        IDomainEvent? paymentResult;
        try
        {
            paymentResult = message.Subject switch
            {
                PaymentCompleted.Type => message.Body.ToObjectFromJson<PaymentCompleted>(JsonSerializerOptions.Web),
                PaymentFailed.Type => message.Body.ToObjectFromJson<PaymentFailed>(JsonSerializerOptions.Web),
                _ => throw new PermanentMessageException($"Unexpected message type '{message.Subject}'.")
            };
        }
        catch (JsonException ex)
        {
            throw new PermanentMessageException($"Malformed {message.Subject} payload.", ex);
        }
        if (paymentResult is null) throw new PermanentMessageException("Empty payload.");

        await handler.HandleAsync(paymentResult, message.MessageId, ct);
    }
}
