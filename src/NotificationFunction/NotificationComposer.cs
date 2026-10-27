using System.Text.Json;
using PayFlow.Contracts;

namespace PayFlow.NotificationFunction;

public sealed record Notification(Guid OrderId, string Subject, string Body);

/// <summary>Turns a payment result event into the email we would send. Pure, so it is trivially testable.</summary>
public static class NotificationComposer
{
    /// <returns>The notification, or null when the message is not a supported payment result or is malformed.</returns>
    public static Notification? TryCompose(string? eventType, BinaryData body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            switch (eventType)
            {
                case PaymentCompleted.Type:
                    var completed = body.ToObjectFromJson<PaymentCompleted>(JsonSerializerOptions.Web);
                    return completed is null ? null : new Notification(
                        completed.OrderId,
                        "Payment confirmed",
                        $"We received your payment of {completed.Amount:0.00} {completed.Currency} for order {completed.OrderId}.");

                case PaymentFailed.Type:
                    var failed = body.ToObjectFromJson<PaymentFailed>(JsonSerializerOptions.Web);
                    return failed is null ? null : new Notification(
                        failed.OrderId,
                        "Payment failed",
                        $"Your payment for order {failed.OrderId} could not be completed ({failed.Reason}).");

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
