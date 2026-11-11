using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Setup product lines: a price word between name and amount is not part of the name.
public class OnboardingProductLineTests
{
    [Theory]
    [InlineData("shirt price 1800", "shirt", 1800)]
    [InlineData("shirt rate 1800", "shirt", 1800)]
    [InlineData("shirt price - 1800", "shirt", 1800)]
    [InlineData("Shirt - 1800", "Shirt", 1800)]
    public void Price_word_is_not_part_of_the_name(string line, string name, decimal price)
    {
        var parsed = ConversationEngine.ParseLooseProductLine(line);
        Assert.NotNull(parsed);
        Assert.Equal(name, parsed!.Name, ignoreCase: true);
        Assert.Equal(price, parsed.Price);
    }

    // A detail word right after the price works without a comma ("stock 1000" from the shop chat).
    [Fact]
    public void Stock_after_price_without_comma_is_a_product()
    {
        Assert.True(CommandParser.TryParseProductLine("Lipstick mattr red - 950 stock 1000", out ProductLine? product));
        Assert.Equal("Lipstick mattr", product!.Name);
        Assert.Equal(950m, product.Price);
        Assert.Equal(1000, product.Extras!.Stock);
        Assert.Equal("red", product.Extras.Color);
    }
}
