namespace PayFlow.OrderService.Domain;

public sealed class Order
{
    private Order() { }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = default!;
    public OrderStatus Status { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Order Create(Guid customerId, decimal amount, string currency, DateTimeOffset now, string? idempotencyKey = null)
    {
        if (customerId == Guid.Empty) throw new DomainException("CustomerId is required.");
        if (amount <= 0) throw new DomainException("Amount must be greater than zero.");
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper))
            throw new DomainException("Currency must be a 3-letter uppercase ISO code.");

        return new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Amount = decimal.Round(amount, 2),
            Currency = currency,
            Status = OrderStatus.Pending,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    // Idempotent: a redelivered PaymentCompleted must not fail or change state.
    public void Confirm(DateTimeOffset now)
    {
        if (Status == OrderStatus.Confirmed) return;
        if (Status != OrderStatus.Pending)
            throw new DomainException($"Cannot confirm an order in status {Status}.");
        Transition(OrderStatus.Confirmed, now);
    }

    public void Reject(DateTimeOffset now)
    {
        if (Status == OrderStatus.Rejected) return;
        if (Status != OrderStatus.Pending)
            throw new DomainException($"Cannot reject an order in status {Status}.");
        Transition(OrderStatus.Rejected, now);
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status == OrderStatus.Cancelled) return;
        if (Status != OrderStatus.Pending)
            throw new DomainException($"Cannot cancel an order in status {Status}.");
        Transition(OrderStatus.Cancelled, now);
    }

    private void Transition(OrderStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }
}
