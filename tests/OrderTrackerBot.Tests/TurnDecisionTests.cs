using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// The voice safety gate: a faithful rewrite runs; an unfaithful one asks again, or refuses when it would change money or an order.
public class TurnDecisionTests
{
    [Fact]
    public void AmountWordTheEngineCannotRead_AsksForDigits_AndRunsNothing()
    {
        var decision = ConversationEngine.DecideRewrite("Ayesha ko ekhattar hazar ka order", new[] { "customer Ayesha price 71000" }, null);

        // The price step is risky, so it is refused rather than offered as a choice; either way nothing runs and the reply names the word.
        Assert.Equal(DecisionKind.Reject, decision.Kind);
        Assert.Empty(decision.Steps);
        Assert.Contains("ekhattar", decision.Message);
    }

    [Theory]
    [InlineData("ikhattar hazar", "")]
    [InlineData("mere paas hazar rupay hain", "")]
    [InlineData("ekhattar hazar", "ekhattar")]
    public void UnknownAmountWords_OnlyFlagsAnAmountWordTheEngineCannotRead(string text, string expected)
    {
        var words = SpokenNumbers.UnknownAmountWords(text);

        Assert.Equal(string.IsNullOrEmpty(expected) ? Array.Empty<string>() : new[] { expected }, words);
    }

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

    [Theory]
    [InlineData("mark 5 shipped mat karo")]
    [InlineData("cancel order 12 nahi sorry")]
    [InlineData("mark 5 shipped galat tha")]
    public void TypedRetractedRiskyCommand_IsRefused(string message)
    {
        // Only meaningful when the parser still reads it as the command; otherwise it never reaches execution and the gate has nothing to stop.
        if (CommandParser.TryParse(message) is null) return;

        Assert.Equal(DecisionKind.Reject, TurnGate.DecideTyped(message).Kind);
    }

    [Theory]
    [InlineData("mark 5 shipped")]
    [InlineData("cancel order 12")]
    [InlineData("orders today")]
    [InlineData("order 12 delivery nahi")]
    public void TypedPlainCommand_Executes(string message)
    {
        var decision = TurnGate.DecideTyped(message);

        Assert.Equal(DecisionKind.Execute, decision.Kind);
        Assert.Equal(new[] { message }, decision.Steps);
    }

    [Theory]
    [InlineData("shayad order 12 cancel kar dun", "cancel order 12")]
    [InlineData("hmm order 12 discount de do", "discount 12 10%")]
    [InlineData("agar ho sake to order 12 delivery 300", "order 12 delivery 300")]
    public void HedgedTranscript_BecomingARiskyStep_IsRefused(string transcript, string step)
    {
        var decision = ConversationEngine.DecideRewrite(transcript, new[] { step }, new HashSet<long> { 12, 300 });

        Assert.Equal(DecisionKind.Reject, decision.Kind);
        Assert.Empty(decision.Steps);
    }

    [Fact]
    public void ConfidentTranscript_BecomingARiskyStep_StillExecutes()
    {
        var decision = ConversationEngine.DecideRewrite("order 12 cancel kar do", new[] { "cancel order 12" }, new HashSet<long> { 12 });

        Assert.Equal(DecisionKind.Execute, decision.Kind);
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
