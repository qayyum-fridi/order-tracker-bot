using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using Xunit;

namespace OrderTrackerBot.Tests;

// A short spoken answer to a yes / no question is read by plain rules, not by the model (the live reset case: "او بھائی میں نے ہاں بولا تھا").
public class VoiceYesNoTests
{
    private static readonly string[] YesNo = { "yes", "no", "reply", "help" };

    [Theory]
    [InlineData("او بھائی میں نے ہاں بولا تھا", "yes")]
    [InlineData("haan kar do", "yes")]
    [InlineData("Haan ji bilkul", "yes")]
    [InlineData("ji theek hai", "yes")]
    [InlineData("nahi rehne do", "no")]
    [InlineData("نہیں رہنے دو", "no")]
    [InlineData("mat karo", "no")]
    public void ShortAnswerWithFiller_IsYesOrNo(string spoken, string expected)
    {
        Assert.Equal(expected, ConversationEngine.SpokenYesNo(spoken, YesNo));
    }

    [Theory]
    [InlineData("haan nahi")]                                   // both said: ambiguous
    [InlineData("haan lekin order teen cancel karo")]           // yes and cancel
    [InlineData("haan 500 rupay")]                              // an amount is never read as a plain answer
    [InlineData("haan haan mujhe yeh sab kuch abhi kar do aur phir dekhte hain")] // longer than 8 words
    [InlineData("orders today")]                                // a command
    [InlineData("kuch aur batao")]                              // neither
    [InlineData("mujhe samajh nahi aa raha kya bolun")]         // a help request, not a NO
    public void AmbiguousOrLongAnswer_IsLeftToTheRewrite(string spoken)
    {
        Assert.Null(ConversationEngine.SpokenYesNo(spoken, YesNo));
    }

    [Fact]
    public void WhenTheQuestionIsNotYesNo_NothingIsRead()
    {
        Assert.Null(ConversationEngine.SpokenYesNo("haan", new[] { "command", "reply", "help" }));
    }

    [Fact]
    public void ResetQuestion_IsAYesNoQuestionForVoice()
    {
        var seller = new Seller { WhatsAppPhoneNumber = "923001234567", OnboardingComplete = true };
        var allowed = ConversationEngine.AllowedVoiceActions(seller, ConversationState.AwaitingResetConfirmation);
        Assert.Equal("yes", ConversationEngine.SpokenYesNo("او بھائی میں نے ہاں بولا تھا", allowed));
    }
}
