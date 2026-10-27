namespace PayFlow.OrderService.Infrastructure.Messaging;

public sealed class ServiceBusOptions
{
    public const string Section = "ServiceBus";

    /// <summary>Local emulator / dev only. In Azure leave empty and use FullyQualifiedNamespace + Managed Identity.</summary>
    public string? ConnectionString { get; init; }
    public string? FullyQualifiedNamespace { get; init; }
}
