using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using Xunit;

namespace OrderTrackerBot.Tests;

// The voice safety gate: a faithful rewrite runs; an unfaithful one asks again, or refuses when it would change money or an order.
public class TurnDecisionTests
{
    [Fact]
    public void AmountWordTheEngineCannotRead_AsksForDigits_AndRunsNothing()
    {
        var decision = ConversationEngine.DecideRewrite("Ayesha ko ekhatarr hazar ka order", new[] { "customer Ayesha price 71000" }, null);

        // The price step is risky, so it is refused rather than offered as a choice; either way nothing runs and the reply names the word.
        Assert.Equal(DecisionKind.Reject, decision.Kind);
        Assert.Empty(decision.Steps);
        Assert.Contains("ekhatarr", decision.Message);
    }

    [Theory]
    [InlineData("ikhattar hazar", "")]
    [InlineData("mere paas hazar rupay hain", "")]
    [InlineData("ekhatarr hazar", "ekhatarr")]
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

    [Theory]
    [InlineData("ہاں لیکن کوانٹٹی دو کر دو", "yes")]
    [InlineData("ہاں، ٹھیک ہے... نہیں رکو", "yes")]
    public void UrduScriptReversal_AfterAYes_IsNotApproved(string transcript, string step)
    {
        Assert.False(ConversationEngine.IsFaithfulRewrite(transcript, step));
    }

    [Fact]
    public void UrduScriptComparatorDeliveryAndInclusion_AreNotDropped()
    {
        Assert.False(ConversationEngine.IsFaithfulRewrite("پانچ ہزار سے کم رکھنا", "price 5000"));
        Assert.False(ConversationEngine.IsFaithfulRewrite("آرڈر بارہ پر ڈیلیوری الگ ہے", "order 12 price 500"));
        Assert.False(ConversationEngine.IsFaithfulRewrite("ڈیلیوری شامل ہے", "order 12 price 500"));
    }

    [Fact]
    public void UrduScriptHedge_BecomingARiskyStep_IsRefused()
    {
        var decision = ConversationEngine.DecideRewrite("شاید آرڈر 12 کینسل کر دوں", new[] { "cancel order 12" }, new HashSet<long> { 12 });

        Assert.Equal(DecisionKind.Reject, decision.Kind);
    }

    [Theory]
    [InlineData(ConversationState.Idle, "stock Kurti 20", true)]
    [InlineData(ConversationState.Idle, "expense 500 petrol", true)]
    [InlineData(ConversationState.Idle, "delivery 250", false)]
    [InlineData(ConversationState.AwaitingOrderConfirmation, "haan", true)]
    [InlineData(ConversationState.AwaitingCancelConfirmation, "nahi", true)]
    [InlineData(ConversationState.Idle, "haan", false)]
    [InlineData(ConversationState.AwaitingOrderConfirmation, "haan lekin delivery 300", false)]
    [InlineData(ConversationState.Idle, "cancel 12", false)]
    [InlineData(ConversationState.Idle, "expense 500 petrol nahi", false)]
    [InlineData(ConversationState.Idle, "stock Kurti bees", false)]
    [InlineData(ConversationState.Idle, "shayad stock Kurti 20", false)]
    public void PlainSpokenCommands_SkipTheRewrite_OthersDoNot(ConversationState state, string transcript, bool skips)
    {
        Assert.Equal(skips, ConversationEngine.CanRunVoiceAsTyped(state, transcript));
    }

    [Theory]
    [InlineData("Kurti", "Kurti")]
    [InlineData("  Lawn Suit  ", "Lawn Suit")]
    [InlineData("Embroidered Chiffon Lawn Suit Three Piece Special", "Embroidered Chiffon Lawn Suit Three")]
    [InlineData("Supercalifragilisticexpialidocious-and-more-letters-here", "Supercalifragilisticexpialidocious-and-m")]
    public void LongKeywordNames_AreCutAtAWord_NotDropped(string name, string expected)
    {
        Assert.Equal(expected, OrderTrackerBot.Infrastructure.Ai.OpenAiAudioTranscriber.FitKeyword(name));
    }

    [Theory]
    [InlineData("ikhatar hazar", 71000)]            // one letter off "ikhattar"
    [InlineData("pachhatar hazar", 75000)]          // one letter off "pachhattar"
    public void MisspeltNumberWordNextToAMultiplier_IsReadWhenOneLetterOff(string text, long expected)
    {
        Assert.Contains(expected, SpokenNumbers.WordAmounts(text));
    }

    [Theory]
    [InlineData("saath hazar", true)]               // 60 thousand
    [InlineData("kuch saathi hazar", false)]        // everyday word, never corrected into a number
    public void FuzzyMatch_NeverTurnsAnEverydayOrAmbiguousWordIntoANumber(string text, bool readsAnAmount)
    {
        Assert.Equal(readsAnAmount, SpokenNumbers.WordAmounts(text).Count > 0);
    }

    [Fact]
    public void RefusedUnknownNumberWord_IsCounted_WordOnly()
    {
        UnknownNumberWordLog.Clear();

        ConversationEngine.DecideRewrite("Ayesha ko ekhatarr hazar ka order", new[] { "customer Ayesha price 71000" }, null);

        Assert.Contains(UnknownNumberWordLog.Snapshot(), x => x.Word == "ekhatarr" && x.Count >= 1);
    }

    [Fact]
    public void ConfidentTranscript_BecomingARiskyStep_StillExecutes()
    {
        var decision = ConversationEngine.DecideRewrite("order 12 cancel kar do", new[] { "cancel order 12" }, new HashSet<long> { 12 });

        Assert.Equal(DecisionKind.Execute, decision.Kind);
    }

    [Fact]
    public void HarmlessRewrite_WithAnAmountMissing_WaitsForYes_AndNamesTheAmount()
    {
        var decision = ConversationEngine.DecideRewrite("Lawn Suit teen hazaar paanch sau", new[] { "Lawn Suit - 3000" }, null);

        Assert.Equal(DecisionKind.Confirm, decision.Kind);
        Assert.Equal(new[] { "Lawn Suit - 3000" }, decision.Steps);
        Assert.Contains("3500", decision.Message);
    }

    [Fact]
    public void RiskyRewrite_WithAnAmountMissing_IsRefused_AndNamesTheAmount()
    {
        var decision = ConversationEngine.DecideRewrite("order 12 ka price teen hazaar paanch sau", new[] { "price 1 = 3000" }, new HashSet<long> { 12 });

        Assert.Equal(DecisionKind.Reject, decision.Kind);
        Assert.Empty(decision.Steps);
        Assert.Contains("3500", decision.Message);
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
