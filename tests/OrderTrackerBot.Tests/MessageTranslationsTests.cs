using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using Xunit;

namespace OrderTrackerBot.Tests;

public class MessageTranslationsTests
{
    [Theory]
    [InlineData(Lang.English)]
    [InlineData(Lang.UrduScript)]
    public void EveryTemplate_HasAHandWrittenTranslation_AndNoExtraKey(string language)
    {
        var texts = MessageTranslations.TextsFor(language);
        var missing = MessageTemplates.All.Keys.Where(k => !texts.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"{language}: no translation for {missing.Count} keys, e.g. {string.Join(", ", missing.Take(10))}");
        Assert.Empty(texts.Keys.Where(k => !MessageTemplates.All.ContainsKey(k)));
    }

    [Theory]
    [InlineData(Lang.English)]
    [InlineData(Lang.UrduScript)]
    public void EveryTranslation_KeepsThePlaceholdersAndTheNumbers(string language)
    {
        foreach (var (key, text) in MessageTranslations.TextsFor(language))
        {
            var roman = MessageTemplates.All[key];
            Assert.True(Sorted(Placeholders(roman)).SequenceEqual(Sorted(Placeholders(text))), $"{language} {key}: placeholders differ");
            Assert.True(Sorted(Digits(roman)).SequenceEqual(Sorted(Digits(text))), $"{language} {key}: numbers differ");
        }
    }

    [Theory]
    [InlineData(Lang.English)]
    [InlineData(Lang.UrduScript)]
    public void ShopWording_CoversEveryKindOfShop(string language)
    {
        var wording = MessageTranslations.ShopWordingFor(language);
        foreach (var profile in IndustryProfiles.All.Append(IndustryProfiles.General))
        {
            Assert.True(wording.ContainsKey(profile.Title), $"{language} {profile.Key}: title");
            Assert.True(wording.ContainsKey(profile.MenuTip), $"{language} {profile.Key}: menu tip");
            Assert.True(wording.ContainsKey(profile.DataFocus), $"{language} {profile.Key}: data focus");
            foreach (var tip in profile.Tips)
                Assert.True(wording.ContainsKey(tip), $"{language} {profile.Key}: tip \"{tip}\"");
        }
    }

    [Fact]
    public void TryTranslate_UsesTheTranslation_AndKeepsTheArguments()
    {
        var roman = MessageTemplates.Get("order-payments.004", "7", "Ayesha", "Rs.500", "", "");
        Assert.True(MessageTranslations.TryTranslate(roman, Lang.English, out var english));
        Assert.Equal("✅ Order #7 (Ayesha): Rs.500 received — now fully PAID.", english);
    }

    [Fact]
    public void TryTranslate_SwapsAShopTipForItsTranslation_InTheMenu()
    {
        var grocery = IndustryProfiles.ByKey("grocery");
        var roman = MessageTemplates.Get("commands.042", grocery.MenuTip, grocery.Title);

        Assert.True(MessageTranslations.TryTranslate(roman, Lang.English, out var english));
        Assert.Contains(MessageTranslations.ShopWordingFor(Lang.English)[grocery.MenuTip], english);
        Assert.DoesNotContain(grocery.MenuTip, english);
    }

    [Fact]
    public void TryTranslate_TranslatesEveryStepOfTheGuide_WhenSentAsOneMessage()
    {
        var steps = new[]
        {
            MessageTemplates.Get("guide.006", MessageTemplates.Get("guide.004", "Hamza, 1 fast charger 20w, 03001234567, Saddar Rawalpindi")),
            MessageTemplates.Get("guide.007"),
            MessageTemplates.Get("guide.008"),
            MessageTemplates.Get("guide.009")
        };
        var roman = string.Join("\n\n", steps);

        Assert.True(MessageTranslations.TryTranslate(roman, Lang.UrduScript, out var urdu));
        Assert.Contains("مرحلہ 1/4", urdu);
        Assert.Contains("مرحلہ 2/4", urdu);
        Assert.Contains("مرحلہ 4/4", urdu);
        Assert.Contains("گاہک کا پیغام", urdu);
        Assert.Contains("03001234567", urdu);
        Assert.DoesNotContain("Customer ka message", urdu);
        Assert.DoesNotContain("Orders Dekhna", urdu);

        Assert.True(MessageTranslations.TryTranslate(roman, Lang.English, out var english));
        Assert.Contains("Step 2/4 — Seeing Orders", english);
        Assert.DoesNotContain("Orders Dekhna", english);
    }

    [Fact]
    public void TryTranslate_TranslatesTheProductStep_WithItsRepeatedPlaceholderAndBusinessSummary()
    {
        var summary = string.Join(", ", "Sargodha", MessageTemplates.Get("onboarding.business", "Food / Bakery"), "@qayyumfareedi");
        var roman = MessageTemplates.Get("onboarding.010", summary,
            MessageTemplates.Get("onboarding.021", MessageTemplates.Get("product.formats", "Brownies Box - 1200")));

        Assert.True(MessageTranslations.TryTranslate(roman, Lang.UrduScript, out var urdu));
        Assert.Contains("کھانا / بیکری کاروبار", urdu);
        Assert.Contains("@qayyumfareedi", urdu);
        Assert.Contains("Brownies Box - 1200, cost 2000, stock 10", urdu);
        Assert.DoesNotContain("Product add karne ka tareeqa", urdu);
        Assert.DoesNotContain("Theek hai, ab products", urdu);
        Assert.DoesNotContain("Sab products ho jayein", urdu);
    }

    [Fact]
    public void TryTranslate_TextNoTemplateMatches_IsLeftForTheAi()
    {
        const string text = "Kuch bhi likha hua, koi template nahi.";
        Assert.False(MessageTranslations.TryTranslate(text, Lang.English, out var translated));
        Assert.Equal(text, translated);
    }

    private static List<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).ToList();

    private static List<string> Digits(string text) =>
        Regex.Matches(text, @"\d+").Select(m => m.Value).ToList();

    private static List<string> Sorted(List<string> values) => values.OrderBy(v => v, StringComparer.Ordinal).ToList();
}
