using Microsoft.Extensions.Logging;
using PayFlow.Contracts;
using PayFlow.PaymentService.Domain;

namespace PayFlow.PaymentService.Application;

public sealed partial class ProcessPaymentHandler(
    IPaymentRepository payments,
    IInbox inbox,
    IPaymentProvider provider,
    IOutbox outbox,
    IUnitOfWork uow,
    TimeProvider clock,
    ILogger<ProcessPaymentHandler> logger)
{
    /// <param name="isLastAttempt">
    /// True when the broker will dead-letter the message if this attempt fails. A still-unavailable
    /// provider then fails the payment (and publishes PaymentFailed) instead of leaving the order stuck.
    /// </param>
    public async Task HandleAsync(OrderCreated e, string messageId, string correlationId, bool isLastAttempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Idempotent consumer, layer 1: already handled this exact message.
        if (await inbox.HasProcessedAsync(messageId, ct))
        {
            LogDuplicate(messageId);
            return;
        }

        // Layer 2: one payment per order, whatever message delivered it. A redelivery after a crash
        // finds the existing payment instead of creating (and charging) a second one.
        var payment = await payments.GetByOrderIdAsync(e.OrderId, ct);
        if (payment is null)
        {
            payment = Payment.Create(e.OrderId, e.Amount, e.Currency, correlationId, clock.GetUtcNow());
            payments.Add(payment);
            await uow.SaveChangesAsync(ct);
        }

        if (!payment.IsFinal)
        {
            payment.StartProcessing(clock.GetUtcNow());
            await uow.SaveChangesAsync(ct);

            await ChargeAsync(payment, e.PaymentToken, isLastAttempt, ct);
        }

        inbox.MarkProcessed(messageId);
        await uow.SaveChangesAsync(ct);
    }

    private async Task ChargeAsync(Payment payment, string token, bool isLastAttempt, CancellationToken ct)
    {
        ChargeResult result;
        try
        {
            // Layer 3: the provider call is idempotent too (key = payment id), so retrying after a
            // crash between "charge succeeded" and "commit" cannot take the money twice.
            result = await provider.ChargeAsync(new ChargeRequest(payment.Id, payment.Amount, payment.Currency, token), ct);
        }
        catch (ProviderUnavailableException ex) when (isLastAttempt)
        {
            LogGivingUp(ex, payment.Id);
            result = new ChargeResult(ChargeOutcome.Declined, null, "Payment provider unavailable");
        }
        // Otherwise the exception propagates: the message is abandoned and redelivered by Service Bus.

        var now = clock.GetUtcNow();
        switch (result.Outcome)
        {
            case ChargeOutcome.Succeeded:
                payment.Complete(result.ProviderReference ?? string.Empty, now);
                outbox.Enqueue(new PaymentCompleted(
                    Guid.NewGuid(), now, payment.OrderId, payment.Id, payment.Amount, payment.Currency),
                    payment.CorrelationId);
                break;

            case ChargeOutcome.Declined:
                payment.Fail(result.Reason ?? "Declined", now);
                outbox.Enqueue(new PaymentFailed(
                    Guid.NewGuid(), now, payment.OrderId, payment.Id, payment.FailureReason!),
                    payment.CorrelationId);
                break;

            case ChargeOutcome.Pending:
                payment.AttachProviderReference(result.ProviderReference ?? string.Empty, now);
                break; // stays Processing until the provider webhook arrives

            default:
                throw new InvalidOperationException($"Unknown charge outcome {result.Outcome}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} already processed, skipping")]
    private partial void LogDuplicate(string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider unavailable on last delivery attempt, failing payment {PaymentId}")]
    private partial void LogGivingUp(Exception ex, Guid paymentId);
}
