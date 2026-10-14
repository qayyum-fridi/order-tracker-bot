using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Templates;
using Xunit;

namespace OrderTrackerBot.Tests;

public class MessageTemplatesTests
{
    [Fact]
    public void EveryTemplate_HasADottedKey_AndText()
    {
        Assert.NotEmpty(MessageTemplates.All);
        foreach (var (key, text) in MessageTemplates.All)
        {
            Assert.Matches(@"^[a-z0-9-]+(\.[a-z0-9]+)+$", key);
            Assert.False(string.IsNullOrWhiteSpace(text), key);
        }
    }

    [Fact]
    public void Placeholders_AreNumberedFromZeroWithoutGaps()
    {
        foreach (var (key, text) in MessageTemplates.All)
        {
            var indexes = Regex.Matches(text, @"\{(\d+)\}")
                .Select(m => int.Parse(m.Groups[1].Value))
                .Distinct()
                .OrderBy(i => i)
                .ToList();
            Assert.Equal(Enumerable.Range(0, indexes.Count), indexes);
        }
    }

    [Fact]
    public void Get_FillsPlaceholdersInOrder_AndLeavesPlainTextAlone()
    {
        var setup = MessageTemplates.Get("business.setup.summary", "Bilal Mart", "Karachi", "Grocery / Kiryana", "—", "Easypaisa 0300", "Roman Urdu");
        Assert.StartsWith("⚙️ Business Setup — Bilal Mart\n📍 City: Karachi\n", setup);
        Assert.Contains("🌐 Language: Roman Urdu", setup);
        Assert.Equal("Roman Urdu", MessageTemplates.Get("language.name.roman"));
    }

    [Fact]
    public void Get_ArgumentTextIsNotScannedForPlaceholders()
    {
        var setup = MessageTemplates.Get("business.setup.summary", "{5}", "City", "Type", "Insta", "Pay", "Lang");
        Assert.StartsWith("⚙️ Business Setup — {5}\n", setup);
    }

    [Fact]
    public void Get_UnknownKey_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => MessageTemplates.Get("no.such.key"));
    }
}
