using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using PayFlow.PaymentService.Application;
using Polly;

namespace PayFlow.PaymentService.Infrastructure.Provider;

/// <summary>
/// Talks to the (fake) payment provider. Retries, per-attempt timeouts and the circuit breaker come from the
/// resilience handler on the HttpClient; the Idempotency-Key header makes those retries safe.
/// </summary>
internal sealed class HttpPaymentProvider(HttpClient http) : IPaymentProvider
{
    private sealed record ProviderRequest(
        [property: JsonPropertyName("paymentId")] Guid PaymentId,
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("token")] string Token);

    private sealed record ProviderResponse(
        [property: JsonPropertyName("providerPaymentId")] string? ProviderPaymentId,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("reason")] string? Reason);

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = JsonContent.Create(new ProviderRequest(
                request.PaymentId, request.Amount, request.Currency, request.PaymentToken))
        };
        message.Headers.Add("Idempotency-Key", request.PaymentId.ToString());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new ProviderUnavailableException("Payment provider request failed.", ex);
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500)
                throw new ProviderUnavailableException($"Payment provider returned {(int)response.StatusCode}.");

            var body = await response.Content.ReadFromJsonAsync<ProviderResponse>(ct);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => new ChargeResult(ChargeOutcome.Succeeded, body?.ProviderPaymentId, null),
                HttpStatusCode.Accepted => new ChargeResult(ChargeOutcome.Pending, body?.ProviderPaymentId, null),
                HttpStatusCode.PaymentRequired => new ChargeResult(ChargeOutcome.Declined, null, body?.Reason ?? "Declined"),
                _ => throw new InvalidOperationException($"Unexpected provider response {(int)response.StatusCode}.")
            };
        }
    }
}
