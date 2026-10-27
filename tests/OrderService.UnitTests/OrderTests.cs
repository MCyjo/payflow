using PayFlow.OrderService.Domain;

namespace PayFlow.OrderService.UnitTests;

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);

    private static Order NewOrder() => Order.Create(Guid.NewGuid(), 149.99m, "EUR", Now);

    [Fact]
    public void Create_StartsPending()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(149.99m, order.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Create_RejectsNonPositiveAmount(decimal amount) =>
        Assert.Throws<DomainException>(() => Order.Create(Guid.NewGuid(), amount, "EUR", Now));

    [Theory]
    [InlineData("eur")]
    [InlineData("EURO")]
    [InlineData("")]
    public void Create_RejectsInvalidCurrency(string currency) =>
        Assert.Throws<DomainException>(() => Order.Create(Guid.NewGuid(), 10m, currency, Now));

    [Fact]
    public void Create_RejectsEmptyCustomer() =>
        Assert.Throws<DomainException>(() => Order.Create(Guid.Empty, 10m, "EUR", Now));

    [Fact]
    public void Confirm_IsIdempotent()
    {
        var order = NewOrder();
        order.Confirm(Now);
        order.Confirm(Now.AddMinutes(1)); // redelivered PaymentCompleted

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Fact]
    public void Cancel_AfterConfirm_Throws()
    {
        var order = NewOrder();
        order.Confirm(Now);

        Assert.Throws<DomainException>(() => order.Cancel(Now));
    }

    [Fact]
    public void Reject_AfterCancel_Throws()
    {
        var order = NewOrder();
        order.Cancel(Now);

        Assert.Throws<DomainException>(() => order.Reject(Now));
    }
}
