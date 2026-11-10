using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class SupportVoiceGuardTests
{
    [Fact]
    public void Contact_number_question_is_not_a_support_request()
    {
        // The live note: "kya aap mujhe contact number bata sakte hain?" was rewritten to "contact support".
        var steps = ConversationEngine.CorrectMarginQuestion("kya aap mujhe contact number bata sakte hain", new[] { "contact support" });
        Assert.Equal(new[] { "guide" }, steps);
    }

    [Fact]
    public void Contact_number_question_in_urdu_is_not_a_support_request()
    {
        var steps = ConversationEngine.CorrectMarginQuestion("کیا آپ مجھے کانٹیکٹ نمبر بتا سکتے ہیں؟", new[] { "contact support" });
        Assert.Equal(new[] { "guide" }, steps);
    }

    [Theory]
    [InlineData("mujhe support se baat karni hai")]
    [InlineData("kisi insaan se baat karao")]
    [InlineData("founder se rabta karna hai")]
    [InlineData("مجھے سپورٹ سے بات کرنی ہے")]
    public void Real_support_request_keeps_contact_support(string transcript)
    {
        var steps = new[] { "contact support" };
        Assert.Equal(steps, ConversationEngine.CorrectMarginQuestion(transcript, steps));
    }

    [Fact]
    public void Other_steps_are_untouched()
    {
        var steps = new[] { "orders today" };
        Assert.Equal(steps, ConversationEngine.CorrectMarginQuestion("kya aap mujhe contact number bata sakte hain", steps));
    }
}
