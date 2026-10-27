using System.Text.Json;
using PayFlow.Contracts;
using PayFlow.NotificationFunction;

namespace PayFlow.NotificationFunction.UnitTests;

public class NotificationComposerTests
{
    private static BinaryData Json<T>(T value) => BinaryData.FromString(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));

    [Fact]
    public void PaymentCompleted_ProducesConfirmation()
    {
        var evt = new PaymentCompleted(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), 149.99m, "EUR");

        var n = NotificationComposer.TryCompose(PaymentCompleted.Type, Json(evt));

        Assert.NotNull(n);
        Assert.Equal("Payment confirmed", n.Subject);
        Assert.Equal(evt.OrderId, n.OrderId);
        Assert.Contains("149.99 EUR", n.Body);
    }

    [Fact]
    public void PaymentFailed_ProducesFailureNotice()
    {
        var evt = new PaymentFailed(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), "card_declined");

        var n = NotificationComposer.TryCompose(PaymentFailed.Type, Json(evt));

        Assert.NotNull(n);
        Assert.Equal("Payment failed", n.Subject);
        Assert.Contains("card_declined", n.Body);
    }

    [Theory]
    [InlineData("OrderCreated")]
    [InlineData(null)]
    [InlineData("")]
    public void UnsupportedType_ReturnsNull(string? type) =>
        Assert.Null(NotificationComposer.TryCompose(type, BinaryData.FromString("{}")));

    [Fact]
    public void MalformedPayload_ReturnsNullInsteadOfThrowing() =>
        Assert.Null(NotificationComposer.TryCompose(PaymentCompleted.Type, BinaryData.FromString("not json")));
}
