using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Setup product lines: a price word between name and amount is not part of the name.
public class OnboardingProductLineTests
{
    [Theory]
    [InlineData("kurti price 1800", "kurti", 1800)]
    [InlineData("kurti rate 1800", "kurti", 1800)]
    [InlineData("kurti price - 1800", "kurti", 1800)]
    [InlineData("Kurti - 1800", "Kurti", 1800)]
    public void Price_word_is_not_part_of_the_name(string line, string name, decimal price)
    {
        var parsed = ConversationEngine.ParseLooseProductLine(line);
        Assert.NotNull(parsed);
        Assert.Equal(name, parsed!.Name, ignoreCase: true);
        Assert.Equal(price, parsed.Price);
    }
}
