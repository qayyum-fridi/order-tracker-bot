using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// The catalog the voice rewrite is shown, and how the guards treat a seller who corrects himself while speaking.
public class VoiceGuardTests
{
    [Fact]
    public void NormalCatalog_IsSentWhole()
    {
        var labels = Enumerable.Range(1, 50).Select(i => $"Product {i}").ToList();
        Assert.Equal(labels, ConversationEngine.VoiceCatalogNames(labels, "kuch bhi"));
    }

    [Fact]
    public void VeryLargeCatalog_KeepsTheSpokenNamesFirst_AndIsCapped()
    {
        var labels = Enumerable.Range(1, 400).Select(i => $"Item {i}").ToList();
        labels.Add("Shirt Blue");
        labels.Add("Shirt Red");

        var sent = ConversationEngine.VoiceCatalogNames(labels, "do shirt bhejni hain");

        Assert.Equal(ConversationEngine.VoiceCatalogLimit, sent.Count);
        Assert.Equal(new[] { "Shirt Blue", "Shirt Red" }, sent.Take(2));
    }

    [Fact]
    public void AnAmountTakenBack_IsKnownAsRetracted()
    {
        Assert.Contains(410L, SpokenNumbers.RetractedAmounts("Ayesha ki Shirt 410 nahi 420 ki hai"));
    }

    [Fact]
    public void CorrectionInSpeech_RewriteWithTheFinalAmountOnly_IsFaithful()
    {
        Assert.True(ConversationEngine.IsFaithfulRewrite("Ayesha ko 2 Shirt nahi 5 Shirt bhejni hain", "Ayesha, 5 Shirt", null));
    }

    [Fact]
    public void RewriteThatInventsAnAmount_IsNotFaithful()
    {
        Assert.False(ConversationEngine.IsFaithfulRewrite("Ayesha ko 2 Shirt bhejni hain", "Ayesha, 2 Shirt, advance 500", null));
    }

    [Fact]
    public void AmountSpokenInWords_IsFaithfulWhenWrittenInDigits()
    {
        Assert.True(ConversationEngine.IsFaithfulRewrite("Shirt ka rate paanch sau kar do", "edit product: Shirt - 500", null));
    }
}
