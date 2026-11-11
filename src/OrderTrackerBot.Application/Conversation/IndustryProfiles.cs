using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Everything that differs per kind of shop in what the bot SAYS: example product lines, an example order, tips and the data worth entering first.
/// Text is Roman Urdu like every other bot message; English / Urdu-script sellers get it through the same translation as all other messages
/// (<c>TranslatingSender</c>), so there is one template per industry, not one per language. Examples are product lines the parser accepts
/// (a test enforces it). Behaviour never changes with the industry: only wording and examples.
/// </summary>
public sealed record IndustryProfile(
    string Key,
    string Title,
    string Aliases,
    string[] ProductLines,
    string OrderExample,
    string[] Tips,
    string DataFocus,
    string MenuTip,
    string[] Units)
{
    /// <summary>One simple example line ("Kurti - 1800"), for short prompts.</summary>
    public string SimpleProduct => ProductLines.Last(l => !l.Contains(',')) ;

    /// <summary>The category word of the full example ("lawn", "rashan"), for "category: ..." hints.</summary>
    public string Category => FullParts.FirstOrDefault(p => p.StartsWith("category ", StringComparison.OrdinalIgnoreCase))?["category ".Length..] ?? "general";

    private string[] FullParts => FullProduct.Split(',', StringSplitOptions.TrimEntries);

    /// <summary>"Chini 5 kg - 950": the first example without its details.</summary>
    public string MainLine => FullParts[0];

    /// <summary>"Chini 5 kg".</summary>
    public string MainName => MainLine.Split(" - ")[0];

    /// <summary>"Chini 5 kg - 950, cost 440": the first example with its cost.</summary>
    public string CostLine => FullParts.Length > 1 ? FullParts[0] + ", " + FullParts[1] : MainLine;

    /// <summary>The product name of <see cref="SimpleProduct"/> ("Chini 5 kg"), for "stock Chini 5 kg 20".</summary>
    public string SimpleProductName => SimpleProduct.Split(" - ")[0];

    /// <summary>The complete line with cost, stock and category, which fills the most reports.</summary>
    public string FullProduct => ProductLines.First(l => l.Contains(','));

    private Regex? _aliases;
    internal bool Matches(string text) => (_aliases ??= new Regex(@"(?<![\p{L}\p{N}])(?:" + Aliases + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).IsMatch(text);
}

/// <summary>
/// The kinds of shop. The wording of each one is a JSON text in the message-template store (the <c>industry</c> rows of
/// <c>MessageTemplateRows</c>): seeded once from <c>Templates/Industries/&lt;key&gt;.json</c>, embedded in this assembly, and changed at runtime
/// from the database (see <c>TemplateStore</c>). <c>order</c> sets the order of the tap list and of matching; <c>general</c> is the fallback
/// for an unknown or empty business type and is not in <see cref="All"/>. The key must equal the file name. A row that does not parse is
/// never used; the shipped text stays.
/// </summary>
public static class IndustryProfiles
{
    private const string ResourcePrefix = "Templates.Industries.";
    private const string JsonExtension = ".json";
    private const string GeneralKey = "general";
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static readonly IReadOnlyDictionary<string, string> EmbeddedJson = LoadEmbeddedJson();
    private static volatile State _state = FromJson(EmbeddedJson);

    private sealed record State(IndustryProfile General, IReadOnlyList<IndustryProfile> All);

    public static IndustryProfile General => _state.General;

    public static IReadOnlyList<IndustryProfile> All => _state.All;

    public static IndustryProfile ByKey(string? key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? General;

    /// <summary>The profile for what the seller wrote as their business type ("Clothing", "kiryana store", "lawn boutique"); <see cref="General"/> when unknown or empty.</summary>
    public static IndustryProfile Resolve(string? businessType)
    {
        if (string.IsNullOrWhiteSpace(businessType)) return General;
        return All.FirstOrDefault(p => p.Matches(businessType)) ?? General;
    }

    /// <summary>The shipped kinds of shop, as JSON text by key (the seed and the fallback for the database rows).</summary>
    internal static IReadOnlyDictionary<string, string> EmbeddedTemplateJson => EmbeddedJson;

    /// <summary>Swaps the kinds of shop in use, from JSON text by key. Only called with texts that parsed (TemplateStore checks each one).</summary>
    internal static void Replace(IReadOnlyDictionary<string, string> json) => _state = FromJson(json);

    /// <summary>True when <paramref name="json"/> is a kind-of-shop template named <paramref name="key"/>; the parsed template otherwise null.</summary>
    internal static bool TryParseTemplate(string key, string json, out IndustryTemplate template)
    {
        template = null!;
        try
        {
            var parsed = JsonSerializer.Deserialize<IndustryTemplate>(json, Options);
            if (parsed is null || !string.Equals(parsed.Key, key, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(parsed.MenuTip)) return false;
            template = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static State FromJson(IReadOnlyDictionary<string, string> json)
    {
        var loaded = new List<(int Order, IndustryProfile Profile)>();
        foreach (var (key, text) in json.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!TryParseTemplate(key, text, out var t))
                throw new InvalidOperationException($"Industry template \"{key}\" is not valid (the key must equal its name and menuTip is required).");
            if (loaded.Any(x => string.Equals(x.Profile.Key, t.Key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Industry template key \"{t.Key}\" is used by more than one template.");
            loaded.Add((t.Order, new IndustryProfile(t.Key, t.Title, t.Aliases, t.ProductLines, t.OrderExample, t.Tips, t.DataFocus, t.MenuTip, t.Units ?? Array.Empty<string>())));
        }
        var general = loaded.SingleOrDefault(x => x.Profile.Key == GeneralKey).Profile
            ?? throw new InvalidOperationException("No general industry template (key \"general\").");
        var all = loaded.Where(x => x.Profile.Key != GeneralKey).OrderBy(x => x.Order).Select(x => x.Profile).ToList();
        return new State(general, all);
    }

    private static IReadOnlyDictionary<string, string> LoadEmbeddedJson()
    {
        var assembly = typeof(IndustryProfiles).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(JsonExtension, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            var fileKey = resource.Substring(ResourcePrefix.Length, resource.Length - ResourcePrefix.Length - JsonExtension.Length);
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Industry template {resource} is missing from the build."));
            var text = reader.ReadToEnd();
            if (!TryParseTemplate(fileKey, text, out _))
                throw new InvalidOperationException($"Industry template {resource}: the key must equal the file name and menuTip is required.");
            result.Add(fileKey, text);
        }
        return result;
    }

    /// <summary>The shape of one kind-of-shop template.</summary>
    internal sealed record IndustryTemplate(
        string Key,
        int Order,
        string Title,
        string Aliases,
        string[] ProductLines,
        string OrderExample,
        string[] Tips,
        string DataFocus,
        string MenuTip,
        string[]? Units);
}
