using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class CommandParserTests
{
    [Theory]
    [InlineData("orders today", CommandKind.OrdersToday)]
    [InlineData("Orders Today", CommandKind.OrdersToday)]
    [InlineData("pending orders", CommandKind.PendingOrders)]
    [InlineData("catalog", CommandKind.Catalog)]
    [InlineData("undo", CommandKind.Undo)]
    [InlineData("help", CommandKind.Help)]
    [InlineData("menu", CommandKind.Menu)]
    [InlineData("discount list", CommandKind.DiscountList)]
    [InlineData("loyal customers", CommandKind.LoyalCustomers)]
    [InlineData("unpaid orders", CommandKind.UnpaidOrders)]
    [InlineData("cod pending", CommandKind.CodPending)]
    [InlineData("trending products", CommandKind.TrendingProducts)]
    [InlineData("slow movers", CommandKind.SlowMovers)]
    public void ParsesSimpleCommands(string message, CommandKind expected)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Kind);
    }

    [Fact]
    public void ParsesMarkStatus_WithOrderNumberAndKeyword()
    {
        var parsed = CommandParser.TryParse("mark 3 shipped");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.MarkStatus, parsed!.Kind);
        Assert.Equal(3, parsed.Number);
        Assert.Equal("shipped", parsed.Text);
    }

    [Fact]
    public void ParsesAddProduct_WithNameAndPrice()
    {
        var parsed = CommandParser.TryParse("add product: Lawn Suit - 3500");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.AddProduct, parsed!.Kind);
        Assert.Equal("Lawn Suit", parsed.Text);
        Assert.Equal(3500m, parsed.Amount);
    }

    [Fact]
    public void ParsesCancelOrder_WithNumber()
    {
        var parsed = CommandParser.TryParse("cancel order 5");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.CancelOrder, parsed!.Kind);
        Assert.Equal(5, parsed.Number);
    }

    [Fact]
    public void ParsesCustomerOrderLookup_RomanUrduPattern()
    {
        var parsed = CommandParser.TryParse("ayesha ka order");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.CustomerOrderLookup, parsed!.Kind);
        Assert.Equal("ayesha", parsed.Text);
    }

    [Fact]
    public void ParsesFuzzyStatusUpdate_BeforeGenericLookup()
    {
        var parsed = CommandParser.TryParse("ayesha ka order deliver ho gaya");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.FuzzyStatusUpdate, parsed!.Kind);
        Assert.Equal("ayesha", parsed.Text);
        Assert.Equal("deliver", parsed.Text2);
    }

    [Fact]
    public void ParsesCreateDiscount_RawTextForFurtherParsing()
    {
        var parsed = CommandParser.TryParse("create discount: EID10, 10 percent, expires 15 days");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.CreateDiscount, parsed!.Kind);
        Assert.Equal("EID10, 10 percent, expires 15 days", parsed.Text);
    }

    [Fact]
    public void FreeformOrderText_DoesNotMatchAnyDeterministicCommand()
    {
        var parsed = CommandParser.TryParse("Sara, 1 kurti, 03009876543, Gulberg Lahore");
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("haan", true)]
    [InlineData("no", false)]
    public void IsAffirmative_RecognisesCommonConfirmations(string message, bool expected)
    {
        Assert.Equal(expected, CommandParser.IsAffirmative(message));
    }
}
