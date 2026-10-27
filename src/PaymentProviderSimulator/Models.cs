namespace PayFlow.PaymentProviderSimulator;

public sealed record ChargeRequest(Guid PaymentId, decimal Amount, string Currency, string Token);

public sealed record ChargeResponse(string? ProviderPaymentId, string Status, string? Reason = null);

public sealed record StoredResponse(int StatusCode, ChargeResponse Body);
