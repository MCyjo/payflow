using Microsoft.Extensions.Logging;
using PayFlow.Contracts;
using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.Application;

/// <summary>
/// Closes the saga: PaymentCompleted confirms the order, PaymentFailed rejects it.
/// Idempotent, so Service Bus redeliveries are harmless.
/// </summary>
public sealed partial class ApplyPaymentResultHandler(
    IOrderRepository orders,
    IInbox inbox,
    IUnitOfWork uow,
    TimeProvider clock,
    ILogger<ApplyPaymentResultHandler> logger)
{
    public async Task HandleAsync(IDomainEvent paymentResult, string messageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paymentResult);

        if (await inbox.HasProcessedAsync(messageId, ct))
        {
            LogDuplicate(messageId);
            return;
        }

        // A missing order is not expected (the event is published after the order commits). Throwing lets the
        // broker retry and finally dead-letter the message, where it is visible to operators.
        var order = await orders.GetAsync(paymentResult.OrderId, ct)
            ?? throw new InvalidOperationException($"Order {paymentResult.OrderId} not found.");

        var now = clock.GetUtcNow();
        try
        {
            switch (paymentResult)
            {
                case PaymentCompleted:
                    order.Confirm(now);
                    break;
                case PaymentFailed:
                    order.Reject(now);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported event {paymentResult.EventType}.");
            }
        }
        catch (DomainException ex)
        {
            // Typically: the customer cancelled while the payment was in flight. The order keeps its state.
            // A completed payment for a cancelled order needs a refund (compensating action, see roadmap).
            LogIgnored(ex, order.Id, paymentResult.EventType, order.Status);
        }

        inbox.MarkProcessed(messageId);
        await uow.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} already processed, skipping")]
    private partial void LogDuplicate(string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId} not changed by {EventType} because it is {Status}")]
    private partial void LogIgnored(Exception ex, Guid orderId, string eventType, OrderStatus status);
}
