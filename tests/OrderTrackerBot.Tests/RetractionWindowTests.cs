using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class RetractionWindowTests
{
    [Fact]
    public void WrongAmount_IsRetracted_WhenAStoryComesBetweenNegationAndCorrection()
    {
        const string voice = "bhai order id paanch sau ek ka bill char sau das tha... nahi... ruko...aslam bhai ka phone aa gaya tha beech mein... haan wo char sau bees tha";

        Assert.Contains(410L, SpokenNumbers.RetractedAmounts(voice));
    }

    [Fact]
    public void PlainAmountWithoutReplacement_IsNotRetracted()
    {
        Assert.DoesNotContain(500L, SpokenNumbers.RetractedAmounts("500 nahi chahiye bas"));
    }
}
