namespace PayFlow.Contracts;

public interface IDomainEvent
{
    Guid EventId { get; }
    string EventType { get; }
    DateTimeOffset OccurredAt { get; }
    Guid OrderId { get; }
}

public sealed record OrderCreated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string Currency,
    string PaymentToken) : IDomainEvent
{
    public const string Type = nameof(OrderCreated);
    public string EventType => Type;
}

public sealed record PaymentCompleted(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    Guid PaymentId,
    decimal Amount,
    string Currency) : IDomainEvent
{
    public const string Type = nameof(PaymentCompleted);
    public string EventType => Type;
}

public sealed record PaymentFailed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid OrderId,
    Guid PaymentId,
    string Reason) : IDomainEvent
{
    public const string Type = nameof(PaymentFailed);
    public string EventType => Type;
}
