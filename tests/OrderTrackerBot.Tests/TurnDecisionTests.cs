using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// The voice safety gate: a faithful rewrite runs; an unfaithful one asks again, or refuses when it would change money or an order.
public class TurnDecisionTests
{
    [Fact]
    public void FaithfulRewrite_Executes()
    {
        var decision = ConversationEngine.DecideRewrite("Panj hazaar ton ghatt rakhna", new[] { "price under 5000" }, null);

        Assert.Equal(DecisionKind.Execute, decision.Kind);
        Assert.Equal(new[] { "price under 5000" }, decision.Steps);
    }

    [Fact]
    public void UnfaithfulRewrite_ThatWouldChangePrice_IsRefused_NotOfferedAsChoices()
    {
        var decision = ConversationEngine.DecideRewrite("Panj hazaar ton ghatt rakhna", new[] { "price 5000" }, null);

        Assert.Equal(DecisionKind.Reject, decision.Kind);
        Assert.Empty(decision.Steps);
        Assert.Null(decision.Options);
    }

    [Fact]
    public void UnfaithfulRewrite_ThatIsHarmless_AsksAgain()
    {
        var decision = ConversationEngine.DecideRewrite("teen nahi, do piece", new[] { "quantity 3" }, null);

        Assert.Equal(DecisionKind.Clarify, decision.Kind);
        Assert.Empty(decision.Steps);
        Assert.False(string.IsNullOrWhiteSpace(decision.Message));
    }
}
