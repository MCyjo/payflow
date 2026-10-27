using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PayFlow.PaymentProviderSimulator;

/// <summary>Delivers signed webhooks and retries on non-2xx, like a real provider does.</summary>
public sealed partial class WebhookSender(IHttpClientFactory httpFactory, IConfiguration config, ILogger<WebhookSender> logger)
{
    private const int MaxAttempts = 3;

    public async Task SendAsync(Guid paymentId, string status, string providerReference, string? reason)
    {
        var url = config["Simulator:WebhookUrl"] ?? "http://localhost:5102/api/webhooks/payment";
        var secret = config["Simulator:WebhookSecret"] ?? "dev-only-webhook-secret";

        var body = JsonSerializer.Serialize(
            new { paymentId, status, providerReference, reason }, JsonSerializerOptions.Web);
        var signature = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

        using var http = httpFactory.CreateClient();
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Signature", signature);

                using var response = await http.SendAsync(request);
                LogDelivery(paymentId, status, (int)response.StatusCode, attempt);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException ex)
            {
                LogFailure(ex, paymentId, attempt);
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook {Status} for {PaymentId} -> HTTP {StatusCode} (attempt {Attempt})")]
    private partial void LogDelivery(Guid paymentId, string status, int statusCode, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook for {PaymentId} failed (attempt {Attempt})")]
    private partial void LogFailure(Exception ex, Guid paymentId, int attempt);
}
