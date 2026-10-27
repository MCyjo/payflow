using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayFlow.PaymentService.Application;
using PayFlow.PaymentService.Infrastructure;
using PayFlow.PaymentService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPaymentInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<PaymentDbContext>("postgres");

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    // Dev convenience only. In Azure, migrations run as a separate deployment step.
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.MigrateAsync();
}

app.MapHealthChecks("/health");

app.MapGet("/api/payments/by-order/{orderId:guid}", async (Guid orderId, IPaymentRepository payments, CancellationToken ct) =>
    await payments.GetByOrderIdAsync(orderId, ct) is { } p
        ? Results.Ok(new
        {
            p.Id,
            p.OrderId,
            p.Amount,
            p.Currency,
            Status = p.Status.ToString(),
            p.ProviderReference,
            p.FailureReason,
            p.UpdatedAt
        })
        : Results.NotFound());

// Provider -> us. Authenticated by an HMAC of the raw body; idempotent because providers retry.
app.MapPost("/api/webhooks/payment", async (HttpRequest request, ProviderWebhookHandler handler, IConfiguration config, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync(ct);

    var secret = config["Webhook:Secret"] ?? throw new InvalidOperationException("Configure Webhook:Secret.");
    var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
    var provided = request.Headers["X-Signature"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided)))
        return Results.Unauthorized();

    ProviderWebhook? webhook;
    try { webhook = JsonSerializer.Deserialize<ProviderWebhook>(body, JsonSerializerOptions.Web); }
    catch (JsonException) { return Results.BadRequest(); }
    if (webhook is null) return Results.BadRequest();

    return await handler.HandleAsync(webhook, ct) switch
    {
        WebhookResult.Applied or WebhookResult.Duplicate => Results.Ok(),   // 2xx stops the provider's retries
        WebhookResult.NotFound => Results.NotFound(),
        _ => Results.UnprocessableEntity()
    };
});

app.Run();

public partial class Program;
