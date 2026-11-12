using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ProductUnitTests
{
    [Theory]
    [InlineData("Lawn - 3500, unit: thaan", "thaan")]
    [InlineData("Lawn - 3500, unit: taan", "thaan")]
    [InlineData("Lawn - 3500, unit: roll", "roll")]
    [InlineData("Lawn - 3500, unit: meter", "meter")]
    [InlineData("Lawn - 3500", "piece")]
    public void UnitAttribute_SetsTheProductUnit(string line, string unit)
    {
        Assert.True(CommandParser.TryParseProductLine(line, out ProductLine? product));
        Assert.Equal(unit, product!.UnitType);
    }

    [Fact]
    public void UnitAttribute_IsNotLeftAsAnOtherAttribute()
    {
        Assert.True(CommandParser.TryParseProductLine("Lawn - 3500, unit: thaan, fabric: cotton", out ProductLine? product));
        Assert.Equal("thaan", product!.UnitType);
        Assert.DoesNotContain("unit", product.Extras?.Attributes?.Keys ?? Enumerable.Empty<string>());
        Assert.Equal("cotton", product.Extras?.Attributes?["fabric"]);
    }
}
