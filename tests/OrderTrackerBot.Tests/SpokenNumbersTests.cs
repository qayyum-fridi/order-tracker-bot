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
}
