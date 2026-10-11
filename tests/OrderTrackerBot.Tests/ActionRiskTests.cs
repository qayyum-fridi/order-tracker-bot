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
    [InlineData("mark 3 shipped")]
    public void MoneyOrStateSteps_AreRisky(string step) => Assert.True(ActionRisk.IsRiskyStep(step));

    [Theory]
    [InlineData("quantity 3")]
    [InlineData("orders today")]
    [InlineData("done")]
    public void HarmlessSteps_AreNotRisky(string step) => Assert.False(ActionRisk.IsRiskyStep(step));
}
