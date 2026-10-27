using Azure.Identity;
using Azure.Messaging.ServiceBus;
using PayFlow.Contracts;
using PayFlow.DlqTool;

const string usage = """
    PayFlow DLQ tool

    Usage: dlqtool <command> [subscription] [options]

    Commands:
      peek <subscription>        List dead-lettered messages without removing them
      resubmit <subscription>    Send dead-lettered messages back to the topic, then remove them from the DLQ
      purge <subscription>       Delete dead-lettered messages
      send-poison                Publish a malformed OrderCreated (it is dead-lettered by payment-service)

    Subscriptions: payment-service, order-service, notification-function, audit

    Options:
      --max <n>      Messages to handle (default 20)
      --new-id       resubmit: give each message a new MessageId (use when still inside the topic's
                     duplicate-detection window; by default the original id is kept so consumers'
                     inbox tables recognise it)

    Connection: set SERVICEBUS_CONNECTION_STRING (emulator / dev) or SERVICEBUS_NAMESPACE
    (e.g. mysb.servicebus.windows.net, uses DefaultAzureCredential).
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(usage);
    return 0;
}

var command = args[0];
var subscription = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
var max = ReadOption("--max", 20);
var newId = args.Contains("--new-id");

var connectionString = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTION_STRING");
var ns = Environment.GetEnvironmentVariable("SERVICEBUS_NAMESPACE");
await using var client = !string.IsNullOrWhiteSpace(connectionString) ? new ServiceBusClient(connectionString)
    : !string.IsNullOrWhiteSpace(ns) ? new ServiceBusClient(ns, new DefaultAzureCredential())
    : throw new InvalidOperationException("Set SERVICEBUS_CONNECTION_STRING or SERVICEBUS_NAMESPACE.");

try
{
    switch (command)
    {
        case "peek":
            await DeadLetters.PeekAsync(client, Require(subscription), max);
            break;
        case "resubmit":
            await DeadLetters.ResubmitAsync(client, Require(subscription), max, newId);
            break;
        case "purge":
            await DeadLetters.PurgeAsync(client, Require(subscription), max);
            break;
        case "send-poison":
            await DeadLetters.SendPoisonAsync(client);
            break;
        default:
            Console.Error.WriteLine($"Unknown command '{command}'.\n\n{usage}");
            return 1;
    }
}
catch (ServiceBusException ex)
{
    Console.Error.WriteLine($"Service Bus error: {ex.Message}");
    return 2;
}
return 0;

string Require(string? value) =>
    value ?? throw new ArgumentException($"'{command}' needs a subscription name.");

int ReadOption(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}
