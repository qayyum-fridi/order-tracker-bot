using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

public class UtteranceMaskerTests
{
    [Theory]
    [InlineData("Sara ko 03001234567 pe bhejo", "{name} ko {phone} pe bhejo")]
    [InlineData("mujhe 500 ka petrol", "mujhe {n} ka petrol")]
    [InlineData("MENU   dikhao", "menu dikhao")]
    [InlineData("+92 300 1234567 par call", "{phone} par call")]
    public void MasksPhonesAmountsAndNames(string input, string expected)
    {
        Assert.Equal(expected, UtteranceMasker.Mask(input, new[] { "Sara" }));
    }

    [Fact]
    public void LongerNameWins_AndNamesMatchWholeWordsOnly()
    {
        Assert.Equal("{name} ka order", UtteranceMasker.Mask("Sara Khan ka order", new[] { "Sara", "Sara Khan" }));
        Assert.Equal("sarah ka order", UtteranceMasker.Mask("sarah ka order", new[] { "Sara" }));
    }

    [Fact]
    public void EmptyAndInvisibleInputGiveNoText()
    {
        Assert.Equal("", UtteranceMasker.Mask(null, Array.Empty<string?>()));
        Assert.Equal("", UtteranceMasker.Mask("   ", Array.Empty<string?>()));
        Assert.Equal("kurti", UtteranceMasker.Mask("​Kurti", Array.Empty<string?>()));
    }

    [Fact]
    public void LongTextIsCut()
    {
        Assert.Equal(200, UtteranceMasker.Mask(new string('a', 500), Array.Empty<string?>()).Length);
    }
}
