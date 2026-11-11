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
                Items = { new AiOrderItemDraft { ProductName = "Shirt", Quantity = 2, StatedUnitPrice = unitPrice } },
            },
        },
    };

    [Fact]
    public void Amount_said_in_digits_is_quoted()
    {
        Assert.Empty(ConversationEngine.UnquotedAmounts(OrderWith(1800), "Ayesha 2 Shirt 1800"));
    }

    [Fact]
    public void Amount_said_in_words_is_quoted()
    {
        Assert.Empty(ConversationEngine.UnquotedAmounts(OrderWith(3200), "Ayesha ko teen hazar do sau wala suit"));
    }

    [Fact]
    public void Amount_never_said_is_reported()
    {
        Assert.Equal(new long[] { 1500 }, ConversationEngine.UnquotedAmounts(OrderWith(1500), "Ayesha 2 Shirt"));
    }

    [Fact]
    public void Zero_delivery_is_not_an_amount_to_check()
    {
        Assert.Empty(ConversationEngine.UnquotedAmounts(OrderWith(1800, delivery: 0), "Ayesha 2 Shirt 1800"));
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

    // Live finding: "do shirt bhejni hain Bilal ko" came back with the catalog price, the saved name and the saved phone, and was refused as "not said".
    [Fact]
    public void Catalog_price_and_saved_customer_details_are_not_unsaid()
    {
        var action = new TurnAction
        {
            Action = TurnAction.CreateOrder,
            Orders = new List<AiOrderDraft>
            {
                new() { CustomerName = "Bilal Ahmed", Phone = "03001234567", Items = { new AiOrderItemDraft { ProductName = "Shirt", Quantity = 2, StatedUnitPrice = 1800 } } },
            },
        };
        const string said = "do shirt bhejni hain Bilal ko";

        Assert.Empty(ConversationEngine.UnquotedAmounts(action, said, new HashSet<long> { 1800 }));
        Assert.Empty(ConversationEngine.UnquotedDetails(action, said, new HashSet<string> { "3001234567" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bilal Ahmed" }));
        // without the records the same answer is still refused
        Assert.Equal(new long[] { 1800 }, ConversationEngine.UnquotedAmounts(action, said));
        Assert.Equal(2, ConversationEngine.UnquotedDetails(action, said).Count);
    }

    [Fact]
    public void A_new_product_price_is_never_taken_from_the_records()
    {
        var action = new TurnAction { Action = TurnAction.AddProduct, Products = new List<AiNewProduct> { new() { Name = "Shawl", Price = 1800 } } };
        Assert.Equal(new long[] { 1800 }, ConversationEngine.UnquotedAmounts(action, "shawl naya product hai", new HashSet<long> { 1800 }));
    }

    [Theory]
    [InlineData("order 3 pohnch gaya", 3, 3)]
    [InlineData("teen number wala wapas aa gaya", 3, 3)]
    [InlineData("تین نمبر آرڈر پہنچ گیا ہے گاہک کو", 3, 3)]
    [InlineData("تین نمبر آرڈر پہنچ گیا ہے گاہک کو", 1, null)]   // the live miss: the model answered order 1
    [InlineData("Sara ka parcel bhej diya", 2, null)]
    [InlineData("order 12 cancel kar do", 12, 12)]
    public void Order_number_is_used_only_when_the_seller_said_it(string message, int fromModel, int? expected)
    {
        var action = new TurnAction { Action = TurnAction.UpdateStatus, OrderId = fromModel, Status = "delivered" };
        Assert.Equal(expected, ConversationEngine.SaidOrderId(action, message));
    }
}
