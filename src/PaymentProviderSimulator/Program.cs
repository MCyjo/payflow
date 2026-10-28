using System.Collections.Concurrent;
using PayFlow.PaymentProviderSimulator;
using PayFlow.ServiceDefaults;

// Fake payment provider modelled on Stripe/Adyen. The test token in the request decides the outcome:
//   tok_success             200 succeeded
//   tok_declined            402 card_declined
//   tok_insufficient        402 insufficient_funds
//   tok_500                 500 always
//   tok_timeout             hangs 30s (client timeout + retry)
//   tok_flaky               500 twice per idempotency key, then 200 (proves retries)
//   tok_async               202, then a "completed" webhook after a few seconds
//   tok_async_declined      202, then a "failed" webhook
//   tok_duplicate_webhook   202, then the same "completed" webhook twice (proves idempotent webhook handling)
var builder = WebApplication.CreateBuilder(args);
builder.AddPayFlowServiceDefaults("payment-provider-simulator");
builder.Services.AddHttpClient();
builder.Services.AddSingleton<WebhookSender>();
builder.Services.AddHealthChecks();
var app = builder.Build();

// Idempotency store: the same Idempotency-Key always returns the same final response, charge happens once.
var responses = new ConcurrentDictionary<string, StoredResponse>();
var attempts = new ConcurrentDictionary<string, int>();

app.MapGet("/health", () => Results.Ok());
app.MapPayFlowHealthChecks(); // /health/live and /health/ready: the paths the Kubernetes probes use

app.MapPost("/payments", async (ChargeRequest request, HttpRequest http, WebhookSender webhooks, IConfiguration config) =>
{
    var key = http.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key))
        return Results.BadRequest(new ChargeResponse(null, "error", "Idempotency-Key header is required."));

    if (responses.TryGetValue(key, out var replay))
    {
        http.HttpContext!.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Json(replay.Body, statusCode: replay.StatusCode);
    }

    var providerId = "prov_" + request.PaymentId.ToString("N")[..12];
    var attempt = attempts.AddOrUpdate(key, 1, (_, n) => n + 1);

    switch (request.Token)
    {
        case "tok_500":
            return Results.Json(new ChargeResponse(null, "error", "internal_error"), statusCode: 500);

        case "tok_timeout":
            await Task.Delay(TimeSpan.FromSeconds(30), http.HttpContext!.RequestAborted);
            return Results.Json(new ChargeResponse(providerId, "succeeded"));

        case "tok_flaky" when attempt <= 2:
            return Results.Json(new ChargeResponse(null, "error", "temporarily_unavailable"), statusCode: 503);
    }

    var (statusCode, body, webhooksToSend) = request.Token switch
    {
        "tok_success" or "tok_flaky" => (200, new ChargeResponse(providerId, "succeeded"), Array.Empty<(string, string?)>()),
        "tok_declined" => (402, new ChargeResponse(null, "declined", "card_declined"), []),
        "tok_insufficient" => (402, new ChargeResponse(null, "declined", "insufficient_funds"), []),
        "tok_async" => (202, new ChargeResponse(providerId, "processing"), [("completed", null)]),
        "tok_async_declined" => (202, new ChargeResponse(providerId, "processing"), [("failed", "card_declined")]),
        "tok_duplicate_webhook" => (202, new ChargeResponse(providerId, "processing"), [("completed", null), ("completed", null)]),
        _ => (402, new ChargeResponse(null, "declined", "invalid_token"), [])
    };

    // First writer wins: concurrent retries with the same key cannot trigger two charges / webhooks.
    var stored = responses.GetOrAdd(key, new StoredResponse(statusCode, body));
    if (ReferenceEquals(stored.Body, body) && webhooksToSend.Length > 0)
    {
        var delay = TimeSpan.FromSeconds(config.GetValue("Simulator:WebhookDelaySeconds", 3));
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            foreach (var (status, reason) in webhooksToSend)
                await webhooks.SendAsync(request.PaymentId, status, providerId, reason);
        });
    }

    return Results.Json(stored.Body, statusCode: stored.StatusCode);
});

app.Run();
