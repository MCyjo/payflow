using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using PayFlow.Contracts;
using PayFlow.IntegrationTests.Support;

namespace PayFlow.IntegrationTests.EndToEnd;

/// <summary>
/// Drives the real system (PostgreSQL + Service Bus emulator in containers) through its public HTTP API and
/// asserts on the eventual outcome, exactly as a client would observe it.
/// </summary>
[Collection(SystemCollectionDefinition.Name)]
public sealed class OrderFlowTests(SystemFixture system)
{
    private sealed record OrderDto(Guid Id, string Status);

    private async Task<Guid> PlaceOrderAsync(string token, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { customerId = Guid.NewGuid(), amount = 100m, currency = "EUR", paymentToken = token })
        };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await system.Order.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"Order creation failed: {response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!.Id;
    }

    private async Task<string> OrderStatusAsync(Guid orderId) =>
        (await system.Order.GetFromJsonAsync<OrderDto>($"/api/orders/{orderId}"))!.Status;

    private Task WaitForStatusAsync(Guid orderId, string expected) =>
        Poll.UntilAsync(async () => await OrderStatusAsync(orderId) == expected, $"order {orderId} to become {expected}");

    private Task<int> PaymentEventCountAsync(Guid orderId, string eventType) =>
        PostgresAdmin.ScalarAsync<int>(system.PaymentsDb,
            "SELECT count(*) FROM \"OutboxMessages\" WHERE \"Type\" = @type AND \"Payload\"->>'orderId' = @order",
            ("type", eventType), ("order", orderId.ToString()));

    [Theory]
    [InlineData("tok_success", "Confirmed")]            // happy path
    [InlineData("tok_declined", "Rejected")]            // business failure
    [InlineData("tok_flaky", "Confirmed")]              // provider 503s twice: HTTP retries + idempotency key
    [InlineData("tok_async", "Confirmed")]              // provider answers 202, result arrives via signed webhook
    [InlineData("tok_async_declined", "Rejected")]      // ... and the webhook can report a failure
    [InlineData("tok_500", "Rejected")]                 // provider down: broker redelivery, then a final failed payment
    public async Task PaymentOutcome_DrivesTheFinalOrderStatus(string token, string expectedStatus)
    {
        var orderId = await PlaceOrderAsync(token);

        Assert.Equal("Pending", await OrderStatusAsync(orderId)); // never confirmed synchronously
        await WaitForStatusAsync(orderId, expectedStatus);
    }

    [Fact]
    public async Task DuplicateWebhookDelivery_PublishesPaymentCompletedOnce()
    {
        var orderId = await PlaceOrderAsync("tok_duplicate_webhook"); // the provider sends the same webhook twice

        await WaitForStatusAsync(orderId, "Confirmed");
        await Task.Delay(TimeSpan.FromSeconds(3)); // give the second delivery time to be (wrongly) applied

        Assert.Equal(1, await PaymentEventCountAsync(orderId, PaymentCompleted.Type));
    }

    [Fact]
    public async Task SameIdempotencyKey_ReturnsTheSameOrder()
    {
        var key = Guid.NewGuid().ToString();

        var first = await PlaceOrderAsync("tok_success", key);
        var second = await PlaceOrderAsync("tok_success", key);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task OrderCancelledWhilePaymentIsInFlight_StaysCancelled()
    {
        var orderId = await PlaceOrderAsync("tok_async");
        using var cancel = await system.Order.PostAsync($"/api/orders/{orderId}/cancel", content: null);
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);

        // The payment still completes (the provider already accepted it) ...
        await Poll.UntilAsync(async () => await PaymentEventCountAsync(orderId, PaymentCompleted.Type) == 1,
            "PaymentCompleted to be published");
        await Task.Delay(TimeSpan.FromSeconds(3)); // ... and Order Service must not resurrect the order.

        Assert.Equal("Cancelled", await OrderStatusAsync(orderId));
    }

    [Fact]
    public async Task RedeliveredOrderCreatedWithNewMessageId_DoesNotChargeTwice()
    {
        var orderId = await PlaceOrderAsync("tok_success");
        await WaitForStatusAsync(orderId, "Confirmed");
        var processedBefore = await PostgresAdmin.ScalarAsync<int>(system.PaymentsDb, "SELECT count(*) FROM \"ProcessedMessages\"");

        // Same business event, different MessageId: the inbox cannot recognise it, the one-payment-per-order rule must.
        var again = new OrderCreated(Guid.NewGuid(), DateTimeOffset.UtcNow, orderId, Guid.NewGuid(), 100m, "EUR", "tok_success");
        await using var client = new ServiceBusClient(system.ServiceBusConnectionString);
        await using var sender = client.CreateSender(MessagingNames.DomainEventsTopic);
        await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(again, JsonSerializerOptions.Web))
        {
            MessageId = again.EventId.ToString(),
            Subject = OrderCreated.Type,
            CorrelationId = "duplicate-test"
        });

        await Poll.UntilAsync(
            async () => await PostgresAdmin.ScalarAsync<int>(system.PaymentsDb, "SELECT count(*) FROM \"ProcessedMessages\"") > processedBefore,
            "the duplicate message to be handled");

        Assert.Equal(1, await PostgresAdmin.ScalarAsync<int>(system.PaymentsDb,
            "SELECT count(*) FROM \"Payments\" WHERE \"OrderId\" = @order", ("order", orderId)));
        Assert.Equal(1, await PaymentEventCountAsync(orderId, PaymentCompleted.Type));
    }

    [Fact]
    public async Task PoisonMessage_IsDeadLetteredImmediatelyWithoutRetries()
    {
        await using var client = new ServiceBusClient(system.ServiceBusConnectionString);
        await using var sender = client.CreateSender(MessagingNames.DomainEventsTopic);
        var messageId = Guid.NewGuid().ToString();
        await sender.SendMessageAsync(new ServiceBusMessage("this is not json")
        {
            MessageId = messageId,
            Subject = OrderCreated.Type,
            CorrelationId = "poison-test"
        });

        await using var dlq = client.CreateReceiver(MessagingNames.DomainEventsTopic, MessagingNames.Subscriptions.PaymentService,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        ServiceBusReceivedMessage? deadLettered = null;
        await Poll.UntilAsync(async () =>
        {
            foreach (var m in await dlq.PeekMessagesAsync(50))
                if (m.MessageId == messageId) deadLettered = m;
            return deadLettered is not null;
        }, "the poison message to reach the dead-letter queue", timeoutSeconds: 30);

        Assert.Equal("PermanentFailure", deadLettered!.DeadLetterReason); // not "MaxDeliveryCountExceeded": no pointless retries
        Assert.Contains("Malformed", deadLettered.DeadLetterErrorDescription, StringComparison.Ordinal);
    }
}
