using PayFlow.PaymentService.Domain;

namespace PayFlow.PaymentService.Application;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Payment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct);
    void Add(Payment payment);
}


public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}

public enum ChargeOutcome
{
    Succeeded,
    Declined,
    /// <summary>Accepted for async processing; the final result arrives via webhook.</summary>
    Pending
}

public sealed record ChargeRequest(Guid PaymentId, decimal Amount, string Currency, string PaymentToken);

public sealed record ChargeResult(ChargeOutcome Outcome, string? ProviderReference, string? Reason);

/// <summary>Transient provider failure (timeout, 5xx, open circuit) that survived the retry policy.</summary>
public sealed class ProviderUnavailableException : Exception
{
    public ProviderUnavailableException() { }
    public ProviderUnavailableException(string message) : base(message) { }
    public ProviderUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

public interface IPaymentProvider
{
    /// <exception cref="ProviderUnavailableException">The provider could not be reached or kept failing.</exception>
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct);
}
