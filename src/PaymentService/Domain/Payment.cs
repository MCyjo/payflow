namespace PayFlow.PaymentService.Domain;

public enum PaymentStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3
}

public sealed class DomainException(string message) : Exception(message);

public sealed class Payment
{
    private Payment() { }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = default!;
    public PaymentStatus Status { get; private set; }
    public string CorrelationId { get; private set; } = default!;
    public string? ProviderReference { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsFinal => Status is PaymentStatus.Completed or PaymentStatus.Failed;

    public static Payment Create(Guid orderId, decimal amount, string currency, string correlationId, DateTimeOffset now)
    {
        if (orderId == Guid.Empty) throw new DomainException("OrderId is required.");
        if (amount <= 0) throw new DomainException("Amount must be greater than zero.");

        return new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            Amount = amount,
            Currency = currency,
            Status = PaymentStatus.Pending,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Pending -> Processing. Calling it again while Processing is a no-op (a redelivered message retries the charge).</summary>
    public void StartProcessing(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Processing) return;
        if (Status != PaymentStatus.Pending)
            throw new DomainException($"Cannot start processing a payment in status {Status}.");
        Transition(PaymentStatus.Processing, now);
    }

    /// <summary>The provider accepted the charge but will report the result later via webhook.</summary>
    public void AttachProviderReference(string reference, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Processing)
            throw new DomainException($"Cannot attach a provider reference in status {Status}.");
        ProviderReference = reference;
        UpdatedAt = now;
    }

    public void Complete(string providerReference, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Completed) return;
        if (Status != PaymentStatus.Processing)
            throw new DomainException($"Cannot complete a payment in status {Status}.");
        ProviderReference = providerReference;
        Transition(PaymentStatus.Completed, now);
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Failed) return;
        if (Status is not (PaymentStatus.Pending or PaymentStatus.Processing))
            throw new DomainException($"Cannot fail a payment in status {Status}.");
        FailureReason = reason;
        Transition(PaymentStatus.Failed, now);
    }

    private void Transition(PaymentStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }
}
