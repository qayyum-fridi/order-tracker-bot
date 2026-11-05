using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Templates;

/// <summary>
/// Runtime seller-facing wording, all held in <c>MessageTemplateRows</c>: the Roman Urdu messages (<c>roman_urdu</c>), the English and Urdu-script
/// translations (<c>english</c>, <c>urdu_script</c>), the kinds of shop (<c>industry</c>, the JSON of each profile) and their English / Urdu-script
/// wording (<c>industry_english</c>, <c>industry_urdu_script</c>, the JSON of each profile's translation). The embedded JSON files are the seed
/// (a missing row is added, an existing row is never overwritten) and the fallback: an inactive row, a key with no row, or a row that does not
/// parse shows the shipped text. <see cref="SyncAsync"/> applies the rows; the scheduler calls it every tick, so a change needs no rebuild.
/// </summary>
public static class TemplateStore
{
    public const string RomanUrdu = "roman_urdu";
    private const string IndustryRomanUrdu = "industry";

    /// <summary>What the last sync applied (every group, flattened), so a sync that changes nothing rebuilds nothing.</summary>
    private static Dictionary<string, string>? _applied;

    public static async Task SyncAsync(IAppDbContext db, CancellationToken ct)
    {
        var rows = await db.MessageTemplateRows.AsNoTracking().ToListAsync(ct);
        var present = rows.Select(r => (r.Language, r.Key)).ToHashSet();

        var added = false;
        foreach (var (language, key, text) in Seed())
            if (present.Add((language, key)))
            {
                db.MessageTemplateRows.Add(new MessageTemplateRow { Language = language, Key = key, Text = text, SeedHash = SeedHash.Of(text) });
                added = true;
            }
        if (added)
        {
            await db.SaveChangesAsync(ct);
            rows = await db.MessageTemplateRows.AsNoTracking().ToListAsync(ct);
        }

        // A shipped text that changed follows into rows nobody edited (their stored seed hash still matches their text); edited rows are left alone.
        var byKey = rows.ToDictionary(r => (r.Language, r.Key));
        var changes = new List<(int Id, string Text, string Hash, bool Update)>();
        foreach (var (language, key, text) in Seed())
        {
            if (!byKey.TryGetValue((language, key), out var row)) continue;
            var shipped = SeedHash.Of(text);
            switch (SeedHash.Decide(row.SeedHash, SeedHash.Of(row.Text), shipped))
            {
                case SeedAction.Stamp: changes.Add((row.Id, text, shipped, false)); break;
                case SeedAction.Update: changes.Add((row.Id, text, shipped, true)); break;
            }
        }
        if (changes.Count > 0)
        {
            foreach (var chunk in changes.Chunk(500))
            {
                var ids = chunk.Select(c => c.Id).ToList();
                var tracked = await db.MessageTemplateRows.Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
                foreach (var change in chunk)
                {
                    var target = tracked[change.Id];
                    if (change.Update) (target.Text, target.UpdatedAt) = (change.Text, DateTime.UtcNow);
                    target.SeedHash = change.Hash;
                }
            }
            await db.SaveChangesAsync(ct);
            rows = await db.MessageTemplateRows.AsNoTracking().ToListAsync(ct);
        }

        var roman = Overlay(MessageTemplates.EmbeddedEntries, rows, RomanUrdu, null);
        var translations = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (language, texts) in MessageTranslations.EmbeddedTranslationTexts)
            translations[language] = Overlay(texts, rows, language, null);
        var industry = Overlay(IndustryProfiles.EmbeddedTemplateJson, rows, IndustryRomanUrdu, IsTemplate);
        var shopJson = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (language, texts) in MessageTranslations.EmbeddedShopJson)
            shopJson[language] = Overlay(texts, rows, ShopLanguage(language), IsShop);

        var applied = Flatten(roman, translations, industry, shopJson);
        if (_applied is not null && SameAs(_applied, applied)) return;

        var shops = new Dictionary<string, IReadOnlyDictionary<string, MessageTranslations.ShopTranslation>>(StringComparer.Ordinal);
        foreach (var (language, texts) in shopJson)
        {
            var parsed = new Dictionary<string, MessageTranslations.ShopTranslation>(StringComparer.Ordinal);
            foreach (var (key, text) in texts)
                if (MessageTranslations.TryParseShop(text, out var shop)) parsed[key] = shop;
            shops[language] = parsed;
        }

        // Roman Urdu first: the kinds of shop and the translation matchers are built from it.
        MessageTemplates.Replace(roman);
        IndustryProfiles.Replace(industry);
        var shopWording = MessageTranslations.BuildShopWording(IndustryProfiles.All.Append(IndustryProfiles.General).ToList(), shops, strict: false);
        MessageTranslations.ReplaceTexts(translations, shopWording);
        _applied = applied;
    }

    /// <summary>Back to the shipped wording (tests).</summary>
    internal static void Reset()
    {
        MessageTemplates.Replace(MessageTemplates.EmbeddedEntries);
        IndustryProfiles.Replace(IndustryProfiles.EmbeddedTemplateJson);
        MessageTranslations.ReplaceTexts(MessageTranslations.EmbeddedTranslationTexts, MessageTranslations.EmbeddedShopWordingFor);
        _applied = null;
    }

    /// <summary>Every shipped text as (database language, key, text), for the rows that do not exist yet.</summary>
    private static IEnumerable<(string Language, string Key, string Text)> Seed()
    {
        foreach (var (key, text) in MessageTemplates.EmbeddedEntries) yield return (RomanUrdu, key, text);
        foreach (var (language, texts) in MessageTranslations.EmbeddedTranslationTexts)
            foreach (var (key, text) in texts) yield return (language, key, text);
        foreach (var (key, text) in IndustryProfiles.EmbeddedTemplateJson) yield return (IndustryRomanUrdu, key, text);
        foreach (var (language, texts) in MessageTranslations.EmbeddedShopJson)
            foreach (var (key, text) in texts) yield return (ShopLanguage(language), key, text);
    }

    /// <summary>The database language of a kind-of-shop translation: "english" -> "industry_english".</summary>
    private static string ShopLanguage(string language) => "industry_" + language;

    private static bool IsTemplate(string key, string text) => IndustryProfiles.TryParseTemplate(key, text, out _);

    private static bool IsShop(string key, string text) => MessageTranslations.TryParseShop(text, out _);

    /// <summary>The shipped texts, with the active rows of <paramref name="language"/> on top (keys with no row keep the shipped text).</summary>
    private static Dictionary<string, string> Overlay(IReadOnlyDictionary<string, string> shipped, IEnumerable<MessageTemplateRow> rows, string language,
        Func<string, string, bool>? valid)
    {
        var result = new Dictionary<string, string>(shipped, StringComparer.Ordinal);
        foreach (var row in rows)
            if (row.Language == language && row.Active && !string.IsNullOrWhiteSpace(row.Text) && (valid is null || valid(row.Key, row.Text)))
                result[row.Key] = row.Text;
        return result;
    }

    private static Dictionary<string, string> Flatten(IReadOnlyDictionary<string, string> roman,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> translations,
        IReadOnlyDictionary<string, string> industry,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> shopJson)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, text) in roman) flat[$"roman|{key}"] = text;
        foreach (var (language, texts) in translations)
            foreach (var (key, text) in texts) flat[$"{language}|{key}"] = text;
        foreach (var (key, text) in industry) flat[$"industry|{key}"] = text;
        foreach (var (language, texts) in shopJson)
            foreach (var (key, text) in texts) flat[$"{language}-shop|{key}"] = text;
        return flat;
    }

    private static bool SameAs(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> next)
    {
        if (current.Count != next.Count) return false;
        foreach (var (key, text) in next)
            if (!current.TryGetValue(key, out var old) || old != text) return false;
        return true;
    }
}
