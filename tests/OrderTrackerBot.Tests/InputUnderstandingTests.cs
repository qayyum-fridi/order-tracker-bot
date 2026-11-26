using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class InputUnderstandingTests
{
    [Fact]
    public void Normalizer_UsesOneRepresentationForCopiedUrduText()
    {
        var normalized = InputTextNormalizer.Normalize("\u200F  آرڈر ۳  شپ ہو گیا  ");

        Assert.Equal("آرڈر 3 شپ ہو گیا", normalized);
    }

    [Fact]
    public void QuantityWithoutOrderMarker_IsNotAnOrderId()
    {
        var action = new TurnAction { Action = TurnAction.UpdateStatus, OrderId = 2, Status = "shipped" };

        Assert.Null(ConversationEngine.SaidOrderId(action, "Ali ko 2 shirt bhej do"));
    }

    [Theory]
    [InlineData("order 2 shipped", 2)]
    [InlineData("2 number order delivered", 2)]
    [InlineData("آرڈر ۳ شپ ہو گیا", 3)]
    public void ExplicitOrderReference_IsAccepted(string message, int expected)
    {
        var action = new TurnAction { Action = TurnAction.UpdateStatus, OrderId = expected, Status = "shipped" };

        Assert.Equal(expected, ConversationEngine.SaidOrderId(action, message));
    }
}