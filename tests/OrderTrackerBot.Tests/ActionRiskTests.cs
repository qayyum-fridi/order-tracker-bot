using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ActionRiskTests
{
    [Theory]
    [InlineData("price 1 = 5000")]
    [InlineData("price 5000")]
    [InlineData("cancel order 12")]
    [InlineData("order 12 delivery 300")]
    [InlineData("payout 11700 TCS orders 1 2 3")]
    [InlineData("drawer open 5000")]
    [InlineData("create discount: EID10 10%")]
    public void MoneyOrStateSteps_AreRisky(string step) => Assert.True(ActionRisk.IsRiskyStep(step));

    // Basic daily operations run from voice without a YES: marking one order shipped or paid, advances, counter sales.
    [Theory]
    [InlineData("mark 3 shipped")]
    [InlineData("mark 3 paid")]
    [InlineData("order 12 advance 500")]
    [InlineData("walkin Kurti 2")]
    public void BasicOperationSteps_AreNotRisky(string step) => Assert.False(ActionRisk.IsRiskyStep(step));

    [Theory]
    [InlineData("quantity 3")]
    [InlineData("orders today")]
    [InlineData("done")]
    public void HarmlessSteps_AreNotRisky(string step) => Assert.False(ActionRisk.IsRiskyStep(step));
}
