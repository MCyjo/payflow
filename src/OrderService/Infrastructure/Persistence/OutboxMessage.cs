namespace PayFlow.OrderService.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }                 // == EventId, used as Service Bus MessageId
    public required string Type { get; init; }    // e.g. "OrderCreated", used as message Subject
    public required string Payload { get; init; }
    public required string CorrelationId { get; init; }
    public string? TraceParent { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
