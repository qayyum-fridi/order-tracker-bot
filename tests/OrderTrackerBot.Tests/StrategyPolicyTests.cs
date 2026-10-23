using OrderTrackerBot.Application.Strategy;
using Xunit;

namespace OrderTrackerBot.Tests;

public class StrategyPolicyTests
{
    [Fact]
    public void NoEvidence_HasNoLowerBound()
    {
        Assert.Equal(0, StrategyPolicy.LowerBound(0, 0));
    }

    [Theory]
    [InlineData(16, 16, 0.80, 0.82)]  // sixteen identical answers: the first to clear 0.80
    [InlineData(15, 15, 0.78, 0.80)]
    [InlineData(10, 10, 0.71, 0.73)]
    [InlineData(9, 10, 0.58, 0.61)]   // one different answer in ten is not enough
    [InlineData(5, 5, 0.55, 0.58)]
    public void LowerBound_IsWilsonScore(int good, int total, double low, double high)
    {
        var bound = StrategyPolicy.LowerBound(good, total);
        Assert.InRange(bound, low, high);
    }

    [Fact]
    public void Pick_ReturnsTheArmWhoseBoundClearsTheThreshold()
    {
        var arm = StrategyPolicy.Pick(new[] { new StrategyPolicy.Count("catalog", 16, 16), new StrategyPolicy.Count("written", 0, 16) });
        Assert.Equal("catalog", arm);
    }

    [Fact]
    public void Pick_ReturnsNullWhenNoArmIsReliable()
    {
        var arm = StrategyPolicy.Pick(new[] { new StrategyPolicy.Count("catalog", 5, 10), new StrategyPolicy.Count("written", 5, 10) });
        Assert.Null(arm);
    }

    [Fact]
    public void Pick_ReturnsNullForOneCorrectionInTen()
    {
        Assert.Null(StrategyPolicy.Pick(new[] { new StrategyPolicy.Count("catalog", 9, 10) }));
    }
}
