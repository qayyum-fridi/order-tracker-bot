using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ProductLineNegativeTests
{
    [Theory]
    [InlineData("Price -500")]
    [InlineData("Discount -100")]
    [InlineData("Qty -1")]
    [InlineData("Stock -5")]
    [InlineData("Keemat -500")]
    [InlineData("Kam -100")]
    [InlineData("Less -100")]
    [InlineData("DC -50")]
    [InlineData("Rate -20")]
    public void BookkeepingWordWithNegativeNumber_IsNotAProductLine(string line)
    {
        Assert.False(CommandParser.TryParseProductLine(line, out _));
        Assert.NotEqual(CommandKind.AddProduct, CommandParser.TryParse(line)?.Kind);
    }

    [Theory]
    [InlineData("Lawn Suit - 3500", "Lawn Suit", 3500)]
    [InlineData("Lawn Suit -3500", "Lawn Suit", 3500)]
    [InlineData("Kurti = 1800", "Kurti", 1800)]
    public void RealProductLines_StillParse(string line, string name, decimal price)
    {
        Assert.True(CommandParser.TryParseProductLine(line, out var product));
        Assert.Equal(name, product!.Name);
        Assert.Equal(price, product.Price);
    }

    [Fact]
    public void ZeroPrice_StaysAllowed_ForAFreeSampleProduct()
    {
        Assert.True(CommandParser.TryParseProductLine("Sample Lawn Suit - 0", out var product));
        Assert.Equal("Sample Lawn Suit", product!.Name);
        Assert.Equal(0m, product.Price);
    }
}
