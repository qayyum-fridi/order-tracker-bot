using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Formatting;

namespace OrderTrackerBot.Application.Templates;

/// <summary>
/// Hand-written English and Urdu-script wording. The files are <c>Templates/Translations/&lt;language&gt;/&lt;file&gt;.json</c>: any grouping of
/// keys is fine (a key must be defined once per language), each key has the same placeholders as its <c>Templates/Messages</c> template.
/// <c>industry-profiles.json</c> holds the kind-of-shop wording.
/// A rendered Roman Urdu message is matched back to its template: the template's fixed text must match the whole message, and what fills
/// the placeholders is kept. A shop title or tip that arrives as an argument is swapped for its hand-written translation. A message that no
/// template matches is not translated here; the caller sends it through the AI translator as before.
/// </summary>
public static class MessageTranslations
{
    private const string ResourcePrefix = "Templates.Translations.";
    private const string JsonExtension = ".json";
    private const string IndustryFile = "industry-profiles.json";
    // A template with less fixed text than this would match almost any message, so it is never matched.
    private const int MinFixedLength = 4;
    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)\}", RegexOptions.Compiled);
    // A line of a multi-line argument: its bullet or number ("• ", "1. ") and the text after it.
    private static readonly Regex LinePrefix = new(@"^(\s*(?:[•\-\*]|\d+[.)])?\s*)(.*)$", RegexOptions.Singleline);

    /// <summary>The languages that have translations (every seller-facing language other than Roman Urdu).</summary>
    internal static readonly string[] Languages = { Lang.English, Lang.UrduScript };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts = LoadTexts();
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ShopWording = LoadShopWording();
    private static readonly IReadOnlyList<Matcher> Matchers = BuildMatchers();

    /// <summary>
    /// The hand-written <paramref name="language"/> text of the template that <paramref name="text"/> was rendered from, with the same arguments.
    /// False when no template matches or it has no translation (the caller then asks the AI).
    /// </summary>
    public static bool TryTranslate(string text, string language, out string translated)
    {
        translated = text;
        if (!Texts.TryGetValue(language, out var texts) || !ShopWording.TryGetValue(language, out var shop)) return false;

        foreach (var matcher in Matchers)
        {
            var match = matcher.Pattern.Match(text);
            if (!match.Success) continue;
            if (!texts.TryGetValue(matcher.Key, out var template)) return false;

            var args = new string[matcher.ArgumentCount];
            for (var i = 0; i < matcher.ArgumentSlots.Length; i++)
            {
                args[matcher.ArgumentSlots[i]] = Localize(match.Groups[i + 1].Value, shop);
            }

            var complete = true;
            translated = PlaceholderPattern.Replace(template, p =>
            {
                var index = int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture);
                if (index < args.Length) return args[index];
                complete = false;
                return p.Value;
            });
            if (complete) return true;
            translated = text;
            return false;
        }
        return false;
    }

    /// <summary>
    /// A shop title or tip passed as an argument is swapped for its translation, alone or one per line behind a bullet ("• tip").
    /// Anything else (product lines, names, amounts) is kept exactly as it was.
    /// </summary>
    private static string Localize(string value, IReadOnlyDictionary<string, string> wording)
    {
        if (wording.TryGetValue(value, out var whole)) return whole;
        if (!value.Contains('\n')) return value;
        return string.Join("\n", value.Split('\n').Select(line =>
        {
            var m = LinePrefix.Match(line);
            return m.Groups[1].Value + (wording.TryGetValue(m.Groups[2].Value, out var translated) ? translated : m.Groups[2].Value);
        }));
    }

    /// <summary>Every key with its hand-written text for <paramref name="language"/> (used by the tests).</summary>
    internal static IReadOnlyDictionary<string, string> TextsFor(string language) =>
        Texts.TryGetValue(language, out var texts) ? texts : new Dictionary<string, string>();

    /// <summary>Roman Urdu shop title, tip and data line to its translation, for <paramref name="language"/> (used by the tests).</summary>
    internal static IReadOnlyDictionary<string, string> ShopWordingFor(string language) =>
        ShopWording.TryGetValue(language, out var shop) ? shop : new Dictionary<string, string>();

    private static IEnumerable<string> TranslationResources(Assembly assembly) =>
        assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(JsonExtension, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>"Templates.Translations.english.commands.json" -> ("english", "commands.json").</summary>
    private static (string Language, string File) Split(string resource)
    {
        var rest = resource.Substring(ResourcePrefix.Length);
        var dot = rest.IndexOf('.');
        return (rest[..dot], rest[(dot + 1)..]);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadTexts()
    {
        var assembly = typeof(MessageTranslations).Assembly;
        var options = new JsonSerializerOptions();
        var loaded = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var resource in TranslationResources(assembly))
        {
            var (language, file) = Split(resource);
            if (file == IndustryFile) continue;
            if (!loaded.TryGetValue(language, out var entries)) loaded[language] = entries = new Dictionary<string, string>(StringComparer.Ordinal);

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Translation file {resource} is missing from the build.");
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, options)
                ?? throw new InvalidOperationException($"Translation file {resource} is empty.");
            foreach (var (key, text) in translations)
            {
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException($"Translation \"{key}\" in {resource} is empty.");
                if (!entries.TryAdd(key, text))
                    throw new InvalidOperationException($"Translation key \"{key}\" in {resource} is already defined in another file.");
            }
        }
        return loaded.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>Pairs each Roman Urdu shop title, tip and data line with its translation, for every kind of shop.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadShopWording()
    {
        var assembly = typeof(MessageTranslations).Assembly;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in Languages)
        {
            var resource = $"{ResourcePrefix}{language}.{IndustryFile}";
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Translation file {resource} is missing from the build.");
            var shops = JsonSerializer.Deserialize<Dictionary<string, ShopTranslation>>(stream, options)
                ?? throw new InvalidOperationException($"Translation file {resource} is empty.");

            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var profile in IndustryProfiles.All.Append(IndustryProfiles.General))
            {
                if (!shops.TryGetValue(profile.Key, out var shop))
                    throw new InvalidOperationException($"Translation file {resource} has no entry for the kind of shop \"{profile.Key}\".");
                pairs.TryAdd(profile.Title, shop.Title);
                pairs.TryAdd(profile.MenuTip, shop.MenuTip);
                pairs.TryAdd(profile.DataFocus, shop.DataFocus);
                for (var i = 0; i < profile.Tips.Length && i < shop.Tips.Length; i++)
                    pairs.TryAdd(profile.Tips[i], shop.Tips[i]);
            }
            result[language] = pairs;
        }
        return result;
    }

    private static IReadOnlyList<Matcher> BuildMatchers() =>
        MessageTemplates.All
            .Select(pair => BuildMatcher(pair.Key, pair.Value))
            .Where(m => m is not null)
            .Select(m => m!)
            .OrderByDescending(m => m.FixedLength)
            .ToArray();

    /// <summary>The Roman template as a regex: the fixed text escaped, each placeholder a capture. Null when the template cannot be matched safely.</summary>
    private static Matcher? BuildMatcher(string key, string template)
    {
        var pattern = new StringBuilder("\\A");
        var slots = new List<int>();
        var fixedLength = 0;
        var last = 0;
        foreach (Match m in PlaceholderPattern.Matches(template))
        {
            var literal = template[last..m.Index];
            pattern.Append(Regex.Escape(literal)).Append("(.*?)");
            fixedLength += literal.Count(c => !char.IsWhiteSpace(c));
            slots.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            last = m.Index + m.Length;
        }
        var tail = template[last..];
        pattern.Append(Regex.Escape(tail)).Append("\\z");
        fixedLength += tail.Count(c => !char.IsWhiteSpace(c));

        if (fixedLength < MinFixedLength || slots.Distinct().Count() != slots.Count) return null;
        var regex = new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return new Matcher(key, regex, fixedLength, slots.ToArray(), slots.Count == 0 ? 0 : slots.Max() + 1);
    }

    private sealed record Matcher(string Key, Regex Pattern, int FixedLength, int[] ArgumentSlots, int ArgumentCount);

    private sealed record ShopTranslation(string Title, string MenuTip, string DataFocus, string[] Tips);
}
