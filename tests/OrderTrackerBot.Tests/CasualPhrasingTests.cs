using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Everyday phrasing rewritten to a command the parser already has (CommandParser.CasualRewrites).
public class CasualPhrasingTests
{
    [Theory]
    [InlineData("2 ka status shipped karo", CommandKind.MarkStatus, 2)]
    [InlineData("2 ka status delivered kar do", CommandKind.MarkStatus, 2)]
    [InlineData("3 cancel karo", CommandKind.CancelOrder, 3)]
    [InlineData("3 ka status cancelled karo", CommandKind.CancelOrder, 3)]
    [InlineData("2 wapas aa gaya", CommandKind.MarkStatus, 2)]
    [InlineData("1 ki details", CommandKind.OrderDetail, 1)]
    [InlineData("order 1 ki tafseel dikhao", CommandKind.OrderDetail, 1)]
    public void OrderPhrasing_ParsesToItsCommand(string message, CommandKind kind, int number)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(number, parsed.Number);
    }

    [Fact]
    public void ReturnedPhrasing_SetsTheReturnedStatus()
    {
        Assert.Equal("returned", CommandParser.TryParse("2 wapas aa gaya")!.Text);
        Assert.Equal("shipped", CommandParser.TryParse("2 ka status shipped karo")!.Text);
    }

    [Fact]
    public void StockAdd_IsAnAddOfTheGivenQuantity()
    {
        var parsed = CommandParser.TryParse("Kurti mein 5 aur aaye");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.Stock, parsed!.Kind);
        Assert.Equal("Kurti", parsed.Text);
        Assert.Equal("add", parsed.Text2);
        Assert.Equal(5m, parsed.Amount);
    }

    [Fact]
    public void PriceChange_IsAnEditOfTheProductPrice()
    {
        var parsed = CommandParser.TryParse("Kurti ka rate 2100 kar do");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.EditProduct, parsed!.Kind);
        Assert.Equal("Kurti", parsed.Text);
        Assert.Equal(2100m, parsed.Amount);
    }

    [Fact]
    public void EmailChange_KeepsTheAddress()
    {
        var parsed = CommandParser.TryParse("email badlo ali@shop.com");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.SetEmail, parsed!.Kind);
        Assert.Equal("ali@shop.com", parsed.Text);
    }

    [Theory]
    [InlineData("catalog bhejo", CommandKind.Catalog)]
    [InlineData("aaj ka hisaab bhejo", CommandKind.TodaysSummary)]
    [InlineData("aaj kitna bika", CommandKind.TodaysSummary)]
    [InlineData("kal wala hisaab", CommandKind.TodaysSummary)]
    [InlineData("sab se zyada kya bika", CommandKind.TrendingProducts)]
    [InlineData("Sara ka record dikhao", CommandKind.CustomerDetail)]
    [InlineData("shortcut band karo", CommandKind.Shortcuts)]
    public void EverydayReports_ParseToTheirCommand(string message, CommandKind kind)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Kind);
    }

    [Theory]
    [InlineData("Bag hata do")]
    [InlineData("Sara dhoondo")]
    [InlineData("reset account ko cancel karo")]
    [InlineData("email badlo")]
    [InlineData("order 2 ko cancel wapas karo")]
    [InlineData("12 ka tracking TCS123")]
    public void PhrasesWithoutAMatchingCommand_StayUnparsed(string message)
    {
        Assert.Null(CommandParser.TryParse(message));
    }
}
