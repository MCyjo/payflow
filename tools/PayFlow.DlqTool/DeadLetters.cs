using Azure.Messaging.ServiceBus;
using PayFlow.Contracts;

namespace PayFlow.DlqTool;

internal static class DeadLetters
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

    private static ServiceBusReceiver DeadLetterReceiver(ServiceBusClient client, string subscription) =>
        client.CreateReceiver(MessagingNames.DomainEventsTopic, subscription,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

    public static async Task PeekAsync(ServiceBusClient client, string subscription, int max)
    {
        await using var receiver = DeadLetterReceiver(client, subscription);
        var messages = await receiver.PeekMessagesAsync(max);

        Console.WriteLine($"{messages.Count} dead-lettered message(s) in {MessagingNames.DomainEventsTopic}/{subscription}");
        foreach (var m in messages)
        {
            var body = m.Body.ToString();
            Console.WriteLine($"""
                ---
                MessageId     : {m.MessageId}
                Subject       : {m.Subject}
                CorrelationId : {m.CorrelationId}
                Reason        : {m.DeadLetterReason}
                Description   : {m.DeadLetterErrorDescription}
                DeliveryCount : {m.DeliveryCount}
                EnqueuedAt    : {m.EnqueuedTime:O}
                Body          : {(body.Length > 200 ? body[..200] + "..." : body)}
                """);
        }
    }

    public static async Task ResubmitAsync(ServiceBusClient client, string subscription, int max, bool newId)
    {
        await using var receiver = DeadLetterReceiver(client, subscription);
        await using var sender = client.CreateSender(MessagingNames.DomainEventsTopic);

        var resubmitted = 0;
        foreach (var received in await receiver.ReceiveMessagesAsync(max, MaxWait))
        {
            var copy = new ServiceBusMessage(received);
            if (newId) copy.MessageId = $"{received.MessageId}:r{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            copy.ApplicationProperties["resubmittedFrom"] = subscription;

            // Send first, then remove from the DLQ: a crash in between duplicates the message (consumers are
            // idempotent) instead of losing it.
            await sender.SendMessageAsync(copy);
            await receiver.CompleteMessageAsync(received);
            resubmitted++;
            Console.WriteLine($"Resubmitted {received.MessageId} ({received.Subject})");
        }
        Console.WriteLine($"{resubmitted} message(s) resubmitted. The topic fans out to every matching subscription; " +
                          "subscribers that already handled the message skip it through their inbox.");
    }

    public static async Task PurgeAsync(ServiceBusClient client, string subscription, int max)
    {
        await using var receiver = DeadLetterReceiver(client, subscription);
        var purged = 0;
        foreach (var received in await receiver.ReceiveMessagesAsync(max, MaxWait))
        {
            await receiver.CompleteMessageAsync(received);
            purged++;
        }
        Console.WriteLine($"{purged} message(s) deleted from the dead-letter queue.");
    }

    /// <summary>A message no consumer can ever process: payment-service dead-letters it on first delivery (no retries).</summary>
    public static async Task SendPoisonAsync(ServiceBusClient client)
    {
        await using var sender = client.CreateSender(MessagingNames.DomainEventsTopic);
        var id = Guid.NewGuid().ToString();
        await sender.SendMessageAsync(new ServiceBusMessage("this is not json")
        {
            MessageId = id,
            Subject = OrderCreated.Type,
            CorrelationId = "poison-demo",
            ContentType = "application/json"
        });
        Console.WriteLine($"Published poison message {id}. Check: dlqtool peek {MessagingNames.Subscriptions.PaymentService}");
    }
}
