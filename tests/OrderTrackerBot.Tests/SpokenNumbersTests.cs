using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class SpokenNumbersTests
{
    private static long[] Words(string text) => SpokenNumbers.WordAmounts(text).OrderBy(x => x).ToArray();

    [Theory]
    [InlineData("teen sau", 300)]
    [InlineData("paanch hazar", 5000)]
    [InlineData("do hazar paanch sau", 2500)]
    [InlineData("ek sau paanch", 105)]
    [InlineData("pandrah sau", 1500)]
    [InlineData("pachees sau", 2500)]
    [InlineData("dedh hazar", 1500)]
    [InlineData("dhai sau", 250)]
    [InlineData("sadhe teen hazar", 3500)]
    [InlineData("do lakh", 200000)]
    [InlineData("three thousand five hundred", 3500)]
    [InlineData("twenty five hundred", 2500)]
    [InlineData("تین سو", 300)]
    [InlineData("تین ہزار پانچ سو", 3500)]
    [InlineData("ڈھائی ہزار", 2500)]
    [InlineData("5 hazar", 5000)]
    public void WordAmounts_ReadsSpokenAmounts(string spoken, long expected) => Assert.Equal(new[] { expected }, Words(spoken));

    [Theory]
    [InlineData("order cancel kar do")]
    [InlineData("kar do")]
    [InlineData("sau")]               // a bare multiplier says nothing
    [InlineData("pehla wala")]
    [InlineData("mere paas char lawn suit hain")]
    public void WordAmounts_IgnoresEverydayWords(string text) => Assert.Empty(Words(text));

    [Fact]
    public void WordAmounts_TwoPlainNumbersInARow_AreNotAdded() => Assert.Equal(new long[] { 500 }, Words("kar do paanch sau"));

    [Theory]
    [InlineData("price 3500", 3500)]
    [InlineData("price 3,500 rupay", 3500)]
    [InlineData("price ٣٥٠٠", 3500)]
    public void DigitAmounts_ReadDigitsIncludingGroupedAndUrdu(string text, long expected) =>
        Assert.Equal(new[] { expected }, SpokenNumbers.DigitAmounts(text).ToArray());

    [Fact]
    public void DigitAmounts_IgnorePhoneLengthAndSmallNumbers() =>
        Assert.Empty(SpokenNumbers.DigitAmounts("03001234567 qty 12 order 99"));

    [Theory]
    [InlineData("order nau nau nau", 999)]
    [InlineData("order one zero five", 105)]
    [InlineData("order 1 0 5", 105)]
    public void DictatedDigitAmounts_ReadsIdsSpokenOneDigitAtATime(string spoken, long expected) =>
        Assert.Equal(new[] { expected }, SpokenNumbers.DictatedDigitAmounts(spoken).ToArray());

    // ---- the rewrite guard ----

    [Theory]
    [InlineData("mere paas 4 lawn suit 3500", "Lawn Suit - 3500", true)]                        // digits kept
    [InlineData("Lawn Suit teen hazar paanch sau", "Lawn Suit - 3500", true)]                   // words -> digits
    [InlineData("Lawn Suit teen hazar paanch sau", "Lawn Suit - 3000", false)]                  // amount changed
    [InlineData("Lawn Suit teen hazar paanch sau", "Lawn Suit - 300", false)]                   // amount changed
    [InlineData("kurti ki price teen sau kar do", "Kurti - 300", true)]
    [InlineData("kurti ki price teen sau kar do", "Kurti - 3000", false)]
    [InlineData("haan kar do", "yes", true)]
    [InlineData("haan kar do", "price 5000", false)]                                              // invented amount
    [InlineData("pehla wala", "1", true)]                                                         // small numbers may change
    [InlineData("tis hazar ka order", "order 3000", false)]                                       // wrong magnitude
    [InlineData("order 3500 ka", "Order 35", false)]                                              // digit run dropped
    [InlineData("order nau nau nau ki price pandrah sau lagao", "edit order 999\nprice 1 = 1500", true)] // id dictated digit by digit
    [InlineData("order nau nau nau ki price pandrah sau lagao", "edit order 9999\nprice 1 = 1500", false)]
    public void IsFaithfulRewrite_ChecksAmountsBothWays(string transcript, string rewrite, bool expected) =>
        Assert.Equal(expected, ConversationEngine.IsFaithfulRewrite(transcript, rewrite));

    [Fact]
    public void IsFaithfulRewrite_AllowsNumbersTheModelWasShown()
    {
        var known = new HashSet<long> { 105, 3500 };
        Assert.True(ConversationEngine.IsFaithfulRewrite("pichla order edit karo", "edit order 105", known));
        Assert.False(ConversationEngine.IsFaithfulRewrite("pichla order edit karo", "edit order 106", known));
        Assert.False(ConversationEngine.IsFaithfulRewrite("pichla order edit karo", "edit order 105"));
    }

    // ---- Pakistani seller phrasing (cases from a review round) ----

    private static string? Why(string transcript, string rewrite) => ConversationEngine.WhyUnfaithful(transcript, rewrite);

    [Fact]
    public void Case_FractionalScales_AndATakenBackAmount()
    {
        const string t = "bhai suno us ka bil sadhe teen hazar nahi dedh hazar banta tha tum ne galti se teen hazar panch sau likh diya hai use sahi kar do";
        Assert.Equal(new long[] { 1500, 3500 }, Words(t));
        Assert.Equal(new long[] { 3500 }, SpokenNumbers.RetractedAmounts(t).ToArray());
        Assert.Null(Why(t, "bill 1500"));                       // the corrected amount alone is faithful
        Assert.Null(Why(t, "bill 3500 aur 1500"));
        Assert.NotNull(Why(t, "bill 3500"));                    // dropping the amount the seller kept is not
    }

    [Fact]
    public void Case_EkDoTeen_IsThreeCounts_NotOneHundredTwentyThree()
    {
        const string t = "ek do teen piece bache hain bas black wale ke aur haan bill mein nau nau nau balance add kar dena purana";
        Assert.Empty(SpokenNumbers.DictatedDigitAmounts(t));
        Assert.NotNull(Why(t, "balance 123 add karo"));          // invented
        Assert.NotNull(Why(t, "balance 999 add karo"));          // no id word before "nau nau nau": ambiguous, so it fails safe
        Assert.Equal(new long[] { 123 }, SpokenNumbers.DictatedDigitAmounts("order ek do teen").ToArray()); // after an id word it is an id
    }

    [Fact]
    public void Case_HardSpellings_And_AGluedWordFailsSafe()
    {
        const string t = "unasi hazar ka maal bhej diya hai pacheess sau advanced aya tha baqi unhattar hazar pansau bacha hai";
        Assert.Equal(new long[] { 2500, 69000, 79000 }, Words(t));          // "pansau" (glued) is not read: 69,000 not 69,500
        Assert.Null(Why(t, "maal 79000 advance 2500 baqi 69000"));
        Assert.NotNull(Why(t, "maal 79000 advance 2500 baqi 69500"));      // rejected rather than guessed
    }

    [Fact]
    public void Case_OrderIdNextToAnAmount()
    {
        const string t = "order id ek sau panch ka total banta hai do hazar char sau paanch rupay chalis rupay delivery alag se hai";
        Assert.Equal(new long[] { 105, 2405 }, Words(t));
        Assert.Null(Why(t, "order 105 total 2405 delivery 40"));
    }

    [Fact]
    public void Case_MixedEnglishAndUrduMultipliers() =>
        Assert.Equal(new long[] { 5000, 340000 }, Words("five thousand ka advance aya hai aur baqi teen lakh forty thousand ka check check bounce ho gaya hai"));

    [Fact]
    public void Case_SpokenAccountNumber_IsNotAnAmount_AndIsMasked()
    {
        const string t = "bhai account number likho double zero triple nine zero ek char";
        Assert.Empty(SpokenNumbers.Amounts(t));
        var masked = SpokenNumbers.MaskSpokenDigits(t);
        Assert.Contains("<phone>", masked);
        Assert.DoesNotContain("double", masked);
        Assert.StartsWith("bhai account number likho", masked);
    }

    [Fact]
    public void Case_SelfCorrection_MayDropTheTakenBackAmount()
    {
        const string t = "shipped mark karo order number char sau das... nahi nahi char sau das nahi order char sau bees tha";
        Assert.Equal(new long[] { 410, 420 }, Words(t));
        Assert.Equal(new long[] { 410 }, SpokenNumbers.RetractedAmounts(t).ToArray());
        Assert.Null(Why(t, "mark shipped order 420"));
        Assert.NotNull(Why(t, "mark shipped order 410"));       // the final value was lost

        const string digits = "mark order 410 nahi 420 shipped";
        Assert.Null(Why(digits, "mark shipped order 420"));       // digit runs follow the same rule
    }

    [Fact]
    public void NoReplacement_MeansNoRetraction() =>
        Assert.Empty(SpokenNumbers.RetractedAmounts("price 500 nahi chahiye order 12 ka total 5000"));

    [Fact]
    public void Case_BigAndSmallAmounts_NothingInvented()
    {
        const string t = "ek lakh bees hazar ka sofa hai us par do sau rupay discount de do bas";
        Assert.Equal(new long[] { 200, 120000 }, Words(t));
        Assert.Null(Why(t, "sofa 120000 discount 200"));
        Assert.NotNull(Why(t, "sofa 119800"));
    }

    [Theory]
    [InlineData("bhai unko bolo sath hazar bhejien pure saat hazar ka maal tha baqi ka sath bad mein dekhein ge", new long[] { 7000, 60000 })]
    [InlineData("uske sath hazar rupay dena", new long[0])]                // "with a thousand rupees", not 60,000
    [InlineData("tumhare ke sath hazar bhejna", new long[0])]
    [InlineData("baqi ka sath bad mein", new long[0])]
    public void Case_SathAsSixtyOrAsWith(string t, long[] expected) => Assert.Equal(expected, Words(t));

    [Fact]
    public void Case_UrduScriptWithDigits()
    {
        const string t = "میرا آرڈر نمبر 501 ہے اور اس کا بل تین ہزار نو سو نوے روپے بنتا ہے";
        Assert.Equal(new long[] { 501 }, SpokenNumbers.DigitAmounts(t).ToArray());
        Assert.Equal(new long[] { 3990 }, Words(t));
        Assert.Null(Why(t, "order 501 bill 3990"));
    }
}
