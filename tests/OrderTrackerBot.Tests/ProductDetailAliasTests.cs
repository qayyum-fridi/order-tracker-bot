using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Fixed-syntax product lines with the seller's shorter words: "buy" = cost, plural sizes and colours as one list.</summary>
public class ProductDetailAliasTests
{
    [Fact]
    public void Buy_is_the_cost_price()
    {
        Assert.True(CommandParser.TryParseProductLine("Surf Excel 1kg - 555, buy 500", out var p));
        Assert.Equal(555m, p!.Price);
        Assert.Equal(500m, p.Extras!.Cost);
    }

    [Fact]
    public void Plural_sizes_are_one_listing_each()
    {
        Assert.True(CommandParser.TryParseProductLine("Men's cotton shirt - 1800, sizes S M L XL", out var p));
        Assert.Equal(new[] { "S", "M", "L", "XL" }, p!.Extras!.SizeList);
    }

    [Fact]
    public void Plural_colours_are_one_listing_each()
    {
        Assert.True(CommandParser.TryParseProductLine("Men's cotton shirt - 1800, colours white black", out var p));
        Assert.Equal(new[] { "white", "black" }, p!.Extras!.ColorList);
    }

    [Fact]
    public void One_colour_stays_a_single_colour()
    {
        Assert.True(CommandParser.TryParseProductLine("Men's cotton shirt - 1800, colours red", out var p));
        Assert.Equal("red", p!.Extras!.Color);
        Assert.Null(p.Extras.ColorList);
    }
}
