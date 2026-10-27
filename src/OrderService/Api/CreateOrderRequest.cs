namespace PayFlow.OrderService.Api;

public sealed record CreateOrderRequest(Guid CustomerId, decimal Amount, string Currency, string? PaymentToken = null);
