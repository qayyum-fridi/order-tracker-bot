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

    [Theory]
    [InlineData("aaj ke orders", CommandKind.OrdersToday, null)]
    [InlineData("آج کے آرڈرز", CommandKind.OrdersToday, null)]
    [InlineData("kal ke orders", CommandKind.OrdersToday, "yesterday")]
    [InlineData("orders yesterday", CommandKind.OrdersToday, "yesterday")]
    [InlineData("کل کے آرڈرز", CommandKind.OrdersToday, "yesterday")]
    [InlineData("pichle mahine ke orders", CommandKind.OrdersToday, "lastmonth")]
    [InlineData("orders last month", CommandKind.OrdersToday, "lastmonth")]
    [InlineData("kal ka summary", CommandKind.TodaysSummary, "yesterday")]
    [InlineData("آج کا خلاصہ", CommandKind.TodaysSummary, null)]
    [InlineData("کل کا خلاصہ", CommandKind.TodaysSummary, "yesterday")]
    [InlineData("last month summary", CommandKind.TodaysSummary, "lastmonth")]
    [InlineData("پچھلے مہینے کا خلاصہ", CommandKind.TodaysSummary, "lastmonth")]
    [InlineData("پینڈنگ آرڈر", CommandKind.PendingOrders, null)]
    [InlineData("ہفتہ وار خلاصہ", CommandKind.WeeklySummary, null)]
    public void ParsesReportPeriods_RomanUrduAndUrduScript(string message, CommandKind kind, string? period)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(period, parsed.Text);
    }

    [Theory]
    [InlineData("mark 3 bhej diya", 3, "shipped")]
    [InlineData("mark 3 deliver ho gaya", 3, "delivered")]
    [InlineData("آرڈر 3 شپ ہو گیا", 3, "shipped")]
    [InlineData("آرڈر ۳ ڈیلیور ہو گیا", 3, "delivered")]
    [InlineData("آرڈر 12 پیڈ", 12, "paid")]
    [InlineData("order 4 shipped", 4, "shipped")]
    [InlineData("mark 3 shipped", 3, "shipped")]
    public void ParsesMarkStatus_Synonyms(string message, int number, string status)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.MarkStatus, parsed!.Kind);
        Assert.Equal(number, parsed.Number);
        Assert.Equal(status, parsed.Text);
    }

    [Fact]
    public void MarkStatus_WithUnknownVerb_FallsThrough()
    {
        Assert.NotEqual(CommandKind.MarkStatus, CommandParser.TryParse("order 3 kurti 2500")?.Kind);
    }

    [Theory]
    [InlineData("Ayesha کا آرڈر", CommandKind.CustomerOrderLookup, "Ayesha")]
    [InlineData("عائشہ کی آرڈر", CommandKind.CustomerOrderLookup, "عائشہ")]
    [InlineData("Ayesha کا ٹریکنگ", CommandKind.TrackingLookup, "Ayesha")]
    public void ParsesUrduScriptCustomerLookups(string message, CommandKind kind, string name)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(name, parsed.Text);
    }

    [Theory]
    [InlineData("Ayesha کا آرڈر ڈیلیور ہو گیا", "deliver")]
    [InlineData("Ayesha کا آرڈر شپ ہو گیا", "ship")]
    [InlineData("ayesha ka order bhej diya", "ship")]
    public void ParsesFuzzyStatusUpdate_UrduWords(string message, string keyword)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.FuzzyStatusUpdate, parsed!.Kind);
        Assert.Equal("Ayesha", parsed.Text, ignoreCase: true);
        Assert.Equal(keyword, parsed.Text2);
    }
}
