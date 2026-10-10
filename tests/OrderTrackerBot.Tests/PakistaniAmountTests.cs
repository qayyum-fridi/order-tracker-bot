using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

// Spoken amounts from the Pakistani corpus: the confirmed misreadings, the Urdu-script spellings, and nearby values that must NOT change.
public class PakistaniAmountTests
{
    [Theory]
    [InlineData("ساڑھے تین ہزار", 3500)]
    [InlineData("سوا تین ہزار", 3250)]
    [InlineData("پونے چار ہزار", 3750)]
    [InlineData("Saadhe teen hazaar", 3500)]
    [InlineData("sarhay teen hazar", 3500)]
    [InlineData("Sawa teen hazaar", 3250)]
    [InlineData("Paune chaar hazaar", 3750)]
    [InlineData("Paune chaar hazar mein de do", 3750)]
    [InlineData("sadhe sau", 150)]
    [InlineData("sawa sau", 125)]
    [InlineData("dedh sau", 150)]
    [InlineData("Saṛhay teen sau", 350)]
    [InlineData("Paune chaar sau", 375)]
    public void FractionWords_ReadTheirQuarterOrHalf(string said, long expected)
    {
        Assert.Contains(expected, SpokenNumbers.Amounts(said));
    }

    [Theory]
    [InlineData("3.5 hazaar", 3500)]
    [InlineData("3.5k", 3500)]
    [InlineData("Rs 3.5k", 3500)]
    [InlineData("2.5k", 2500)]
    [InlineData("1.5k", 1500)]
    [InlineData("10k", 10000)]
    [InlineData("3k", 3000)]
    [InlineData("3 hazar", 3000)]
    public void DecimalAndWholeThousands_ReadAsOneAmount(string said, long expected)
    {
        Assert.Equal(new long[] { expected }, SpokenNumbers.Amounts(said).OrderBy(a => a).ToArray());
    }

    [Theory]
    [InlineData("3 hazar 500", 3500)]
    [InlineData("3 hazar 5", 3005)]
    public void ThousandThenDigits_IsOneAmount(string said, long expected)
    {
        Assert.Equal(new long[] { expected }, SpokenNumbers.Amounts(said).OrderBy(a => a).ToArray());
    }

    [Fact]
    public void ThousandThenDigits_DoesNotSwallowASecondPrice()
    {
        Assert.Equal(new long[] { 200, 3500 }, SpokenNumbers.Amounts("3 hazar 500 aur 200 delivery").OrderBy(a => a).ToArray());
    }

    [Theory]
    [InlineData("Sadhe teen baje mil jaana")]    // a time, not an amount: no multiplier
    [InlineData("paune do baje")]
    [InlineData("sawa do baje")]
    [InlineData("3.5 kg atta")]                  // kilograms, not thousands
    [InlineData("3kurti")]
    [InlineData("Kurti 3 piece")]
    public void NearbyPhrases_AreNotAmounts(string said)
    {
        Assert.Empty(SpokenNumbers.Amounts(said));
    }
}
