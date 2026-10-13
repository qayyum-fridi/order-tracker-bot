using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class MarginVoiceGuardTests
{
    // The 24-second voice note from the live test (transcribed text as the bot echoed it).
    private const string LiveTranscript =
        "مجھے ایسے پروڈکٹس بتائیں جن میں اگر میں ڈسکاؤنٹ دوں تو مجھے اچھا مارجن مل سکے۔ پروفیٹ مارجن، نٹ سیل اور ان کی یہ بھی مجھے بتا دیں کہ یہ کتنے ڈسکاؤنٹ پر آئٹم مجھے دینا چاہیے تو میری اچھی سیل ہو سکے۔ یہ مجھے ایک لسٹ بنا دیں۔";

    [Fact]
    public void TheLiveVoiceNote_BecomesTheMarginReport_NotCreateDiscount()
    {
        var steps = ConversationEngine.CorrectMarginQuestion(LiveTranscript, new[] { "create discount" });

        Assert.Equal(new[] { "margin" }, steps);
    }

    [Theory]
    [InlineData("which products give me good margin if I give a discount")]
    [InlineData("discount dun to profit kitna bachega")]
    [InlineData("munafa ke hisaab se discount batao")]
    public void MarginWords_InAnyScript_RedirectTheBareHowTo(string transcript) =>
        Assert.Equal(new[] { "margin" }, ConversationEngine.CorrectMarginQuestion(transcript, new[] { "Create discount" }));

    [Theory]
    [InlineData("eid ke liye discount code banao")]                 // no margin word
    [InlineData("discount banao margin ke saath 10 percent")]       // a percent: this makes a code
    [InlineData("margin dekh kar EID10 code bana do")]              // a code
    [InlineData("مارجن دیکھ کر ڈسکاؤنٹ کوڈ بنائیں")]                // "code" in Urdu script
    public void ARealRequestToMakeACode_IsLeftAlone(string transcript)
    {
        var steps = new[] { "create discount" };
        Assert.Equal(steps, ConversationEngine.CorrectMarginQuestion(transcript, steps));
    }

    [Fact]
    public void OtherSteps_AreNeverTouched()
    {
        var steps = new[] { "orders today", "create discount: EID10, 10 percent" };
        Assert.Equal(steps, ConversationEngine.CorrectMarginQuestion("profit dikhao", steps));
    }

    [Fact]
    public void DuplicateStepsAfterTheSwap_AreCollapsed() =>
        Assert.Equal(new[] { "margin" }, ConversationEngine.CorrectMarginQuestion("margin batao", new[] { "margin", "create discount" }));
}
