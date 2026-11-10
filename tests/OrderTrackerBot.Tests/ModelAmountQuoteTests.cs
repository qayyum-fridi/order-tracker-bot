using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ModelAmountQuoteTests
{
    private static TurnAction OrderWith(decimal? unitPrice, decimal? delivery = null) => new()
    {
        Action = TurnAction.CreateOrder,
        Orders = new List<AiOrderDraft>
        {
            new()
            {
                CustomerName = "Ayesha",
                DeliveryCharge = delivery,
                Items = { new AiOrderItemDraft { ProductName = "Kurti", Quantity = 2, StatedUnitPrice = unitPrice } },
            },
        },
    };

    [Fact]
    public void Amount_said_in_digits_is_quoted()
    {
        Assert.Empty(ConversationEngine.UnquotedAmounts(OrderWith(1800), "Ayesha 2 Kurti 1800"));
    }

    [Fact]
    public void Amount_said_in_words_is_quoted()
    {
        Assert.Empty(ConversationEngine.UnquotedAmounts(OrderWith(3200), "Ayesha ko teen hazar do sau wala suit"));
    }

    [Fact]
    public void Amount_never_said_is_reported()
    {
        Assert.Equal(new long[] { 1500 }, ConversationEngine.UnquotedAmounts(OrderWith(1500), "Ayesha 2 Kurti"));
    }

    [Fact]
    public void Zero_delivery_is_not_an_amount_to_check()
    {
        Assert.Empty(ConversationEngine.UnquotedAmounts(OrderWith(1800, delivery: 0), "Ayesha 2 Kurti 1800"));
    }

    [Fact]
    public void Quantities_are_not_checked()
    {
        var action = new TurnAction
        {
            Action = TurnAction.AddProduct,
            Products = new List<AiNewProduct> { new() { Name = "Shawl", Price = 2500, Cost = 1900 } },
        };
        Assert.Equal(new long[] { 2500, 1900 }, ConversationEngine.UnquotedAmounts(action, "shawl 3 piece naya hai"));
    }
}
