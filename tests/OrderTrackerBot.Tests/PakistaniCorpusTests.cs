using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Starter corpus of realistic Pakistani voice/text lines (numbered as in the corpus). Each expected value is the amount the seller meant.
public class PakistaniCorpusTests
{
    [Theory]
    [InlineData("Saṛhay teen hazaar mein mil jayega?", 3500)]    // 25
    [InlineData("Saadhe teen hazaar", 3500)]
    [InlineData("sāṛhe teen hazaar", 3500)]
    [InlineData("Sawa teen hazaar", 3250)]
    [InlineData("Paune chaar hazaar", 3750)]                       // 92
    [InlineData("Saṛhay teen sau", 350)]
    [InlineData("Bhai 3.5k last price hai?", 3500)]               // 26
    [InlineData("3.5 hazaar", 3500)]
    [InlineData("3k", 3000)]
    [InlineData("Paintis sau mein de do.", 3500)]                  // 24
    [InlineData("Teen hazaar paanch sau ka hai na?", 3500)]        // 23
    [InlineData("Bhai teen hazaar paanch sau ka order bana do, delivery alag hai", 3500)] // 91
    public void Amount_IsReadAsTheSellerMeantIt(string said, long expected)
    {
        Assert.Contains(expected, SpokenNumbers.Amounts(said));
    }

    [Fact]
    public void ThreeHazarFiveHundred_IsOneAmount_NotTwo()
    {
        Assert.Equal(new long[] { 3500 }, SpokenNumbers.Amounts("3 hazar 500").OrderBy(a => a).ToArray());
    }

    [Fact]
    public void TwoAmountsInOneLine_AreBothRead()                  // 98
    {
        Assert.Equal(new long[] { 500, 3000 }, SpokenNumbers.Amounts("Teen hazaar ka item hai aur paanch sau delivery, total kitna hua?").OrderBy(a => a).ToArray());
    }

    [Fact]
    public void RefusedAmount_WithoutReplacement_IsNegated()
    {
        Assert.Contains(5000L, SpokenNumbers.NegatedAmounts("Paanch hazaar mat karna"));
    }

    [Fact]
    public void CorrectedAmount_WithReplacement_IsRetractedNotNegated()  // 99
    {
        const string said = "Nahi nahi, paanch hazaar mat likhna, teen hazaar paanch sau likho";
        Assert.Contains(5000L, SpokenNumbers.RetractedAmounts(said));
        Assert.Contains(3500L, SpokenNumbers.Amounts(said));
    }

    [Theory]
    [InlineData("Paanch hazaar mat karna", "price 5000", false)]                                   // 49-style refusal
    [InlineData("Nahi nahi, paanch hazaar mat likhna, teen hazaar paanch sau likho", "price 5000", false)] // 99
    [InlineData("Nahi nahi, paanch hazaar mat likhna, teen hazaar paanch sau likho", "price 3500", true)]  // 99
    [InlineData("Haan lekin quantity do kar do", "yes", false)]                                       // 86
    [InlineData("Haan, theek hai... nahi ruko bhai, pehle mujhse confirm kar lena", "yes", false)]     // 100
    [InlineData("Haan bhai order kar do", "yes", true)]
    [InlineData("Price 3500 nahi, 5300 hai", "price 3500", false)]                                    // 47
    [InlineData("Price 3500 nahi, 5300 hai", "price 5300", true)]                                     // 47
    [InlineData("Nahi ji, paintis sau nahi, paune chaar hazaar likhna", "price 3750", true)]          // 92
    [InlineData("Nahi ji, paintis sau nahi, paune chaar hazaar likhna", "price 3500", false)]         // 92
    [InlineData("Teen hazaar ka item hai aur paanch sau delivery, total kitna hua?", "price 3000 delivery 500", true)] // 98
    public void Rewrite_KeepsTheSellersMeaning(string said, string rewrite, bool faithful)
    {
        Assert.Equal(faithful, ConversationEngine.IsFaithfulRewrite(said, rewrite));
    }
}
