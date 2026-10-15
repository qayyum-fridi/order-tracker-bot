using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Price groups (A / B / C): product price levels, and the customer group that picks one.</summary>
public class PriceGroupTests
{
    private static Product Shirt(decimal? a = null, decimal? b = null, decimal? c = null) =>
        new() { Name = "Polo Shirt", Price = 500, PriceA = a, PriceB = b, PriceC = c };

    [Theory]
    [InlineData(null, 500)]
    [InlineData("Z", 500)]
    [InlineData("A", 450)]
    [InlineData("B", 420)]
    [InlineData("C", 500)]
    public void PriceFor_UsesTheGroupLevel_OrTheRegularPriceWhenNotSet(string? group, decimal expected)
    {
        var product = Shirt(a: 450, b: 420);

        Assert.Equal(expected, product.PriceFor(group));
    }

    [Fact]
    public void ProductLine_ReadsPriceLevelsFromDetails()
    {
        Assert.True(CommandParser.TryParseProductLine("Polo Shirt - 500, price B 450, price C 380", out var line));

        Assert.Equal(500m, line!.Price);
        Assert.Equal(450m, line.Extras!.PriceB);
        Assert.Equal(380m, line.Extras.PriceC);
        Assert.Null(line.Extras.PriceA);
    }

    [Theory]
    [InlineData("Sara ka group B", "B")]
    [InlineData("customer Sara group A", "A")]
    [InlineData("Sara ka price group c", "C")]
    [InlineData("Sara ka group none", "regular")]
    public void CustomerGroup_ParsesAsACustomerUpdate(string message, string group)
    {
        var cmd = CommandParser.TryParse(message);

        Assert.NotNull(cmd);
        Assert.Equal(CommandKind.CustomerUpdate, cmd!.Kind);
        Assert.Equal("group", cmd.Text2);
        Assert.Equal(group, cmd.Text3);
    }

    [Theory]
    [InlineData("Sara ka group Z")]
    [InlineData("Sara ka group kya hai")]
    public void CustomerGroup_UnknownOrQuestion_IsNotACustomerUpdate(string message)
    {
        var cmd = CommandParser.TryParse(message);

        Assert.False(cmd?.Kind == CommandKind.CustomerUpdate);
    }
}
