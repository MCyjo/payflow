using PayFlow.Contracts;
using PayFlow.PaymentService.Domain;

namespace PayFlow.PaymentService.Application;

public sealed record ProviderWebhook(Guid PaymentId, string Status, string? ProviderReference, string? Reason);

public enum WebhookResult { Applied, Duplicate, NotFound, Rejected, Conflict }

/// <summary>
/// Applies the provider's asynchronous result. Providers retry webhooks and may deliver them out of order,
/// so every transition here is idempotent.
/// </summary>
public sealed class ProviderWebhookHandler(
    IPaymentRepository payments,
    IOutbox outbox,
    IUnitOfWork uow,
    TimeProvider clock)
{
    public async Task<WebhookResult> HandleAsync(ProviderWebhook webhook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(webhook);

        var payment = await payments.GetByIdAsync(webhook.PaymentId, ct);
        if (payment is null) return WebhookResult.NotFound;

        var now = clock.GetUtcNow();
        try
        {
            switch (webhook.Status.ToLowerInvariant())
            {
                case "completed":
                    if (payment.Status == PaymentStatus.Completed) return WebhookResult.Duplicate;
                    payment.Complete(webhook.ProviderReference ?? string.Empty, now);
                    outbox.Enqueue(new PaymentCompleted(
                        Guid.NewGuid(), now, payment.OrderId, payment.Id, payment.Amount, payment.Currency),
                        payment.CorrelationId);
                    break;

                case "failed":
                    if (payment.Status == PaymentStatus.Failed) return WebhookResult.Duplicate;
                    payment.Fail(webhook.Reason ?? "Declined", now);
                    outbox.Enqueue(new PaymentFailed(
                        Guid.NewGuid(), now, payment.OrderId, payment.Id, payment.FailureReason!),
                        payment.CorrelationId);
                    break;

                default:
                    return WebhookResult.Rejected;
            }
        }
        catch (DomainException)
        {
            return WebhookResult.Rejected; // e.g. "failed" arriving for an already completed payment
        }

        try { await uow.SaveChangesAsync(ct); }
        catch (ConcurrencyConflictException)
        {
            // A concurrent delivery of the same webhook won. Ask the provider to retry; the retry sees the final state.
            return WebhookResult.Conflict;
        }
        return WebhookResult.Applied;
    }
}
