using System.Reflection;
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
    string[] Units)
{
    /// <summary>One simple example line ("Kurti - 1800"), for short prompts.</summary>
    public string SimpleProduct => ProductLines.Last(l => !l.Contains(',')) ;

    /// <summary>The category word of the full example ("lawn", "rashan"), for "category: ..." hints.</summary>
    public string Category => FullParts.FirstOrDefault(p => p.StartsWith("category ", StringComparison.OrdinalIgnoreCase))?["category ".Length..] ?? "general";

    private string[] FullParts => FullProduct.Split(',', StringSplitOptions.TrimEntries);

    /// <summary>"Sugar 5 kg - 500": the first example without its details.</summary>
    public string MainLine => FullParts[0];

    /// <summary>"Sugar 5 kg".</summary>
    public string MainName => MainLine.Split(" - ")[0];

    /// <summary>"Sugar 5 kg - 500, cost 440": the first example with its cost.</summary>
    public string CostLine => FullParts.Length > 1 ? FullParts[0] + ", " + FullParts[1] : MainLine;

    /// <summary>The product name of <see cref="SimpleProduct"/> ("Sugar 5 kg"), for "stock Sugar 5 kg 20".</summary>
    public string SimpleProductName => SimpleProduct.Split(" - ")[0];

    /// <summary>The complete line with cost, stock and category, which fills the most reports.</summary>
    public string FullProduct => ProductLines.First(l => l.Contains(','));

    private Regex? _aliases;
    internal bool Matches(string text) => (_aliases ??= new Regex(@"(?<![\p{L}\p{N}])(?:" + Aliases + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).IsMatch(text);
}

/// <summary>
/// The kinds of shop. The wording of each one is its own template file, <c>Templates/Industries/&lt;key&gt;.json</c>, embedded in this assembly:
/// change an example or a tip there, or add a new kind of shop by adding a file (no C# change). <c>order</c> sets the order of the tap list
/// and of matching; <c>general.json</c> is the fallback for an unknown or empty business type and is not in <see cref="All"/>.
/// The file name must equal its <c>key</c>. A broken file fails the tests (and the first use) with the file name in the message.
/// </summary>
public static class IndustryProfiles
{
    private const string ResourcePrefix = "Templates.Industries.";
    private const string JsonExtension = ".json";
    private const string GeneralKey = "general";

    private static readonly IReadOnlyList<(int Order, IndustryProfile Profile)> Loaded = LoadTemplates();

    public static readonly IndustryProfile General = Loaded.Single(x => x.Profile.Key == GeneralKey).Profile;

    public static readonly IReadOnlyList<IndustryProfile> All = Loaded
        .Where(x => x.Profile.Key != GeneralKey)
        .OrderBy(x => x.Order)
        .Select(x => x.Profile)
        .ToList();

    public static IndustryProfile ByKey(string? key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? General;

    /// <summary>The profile for what the seller wrote as their business type ("Clothing", "kiryana store", "lawn boutique"); <see cref="General"/> when unknown or empty.</summary>
    public static IndustryProfile Resolve(string? businessType)
    {
        if (string.IsNullOrWhiteSpace(businessType)) return General;
        return All.FirstOrDefault(p => p.Matches(businessType)) ?? General;
    }

    private static IReadOnlyList<(int Order, IndustryProfile Profile)> LoadTemplates()
    {
        var assembly = typeof(IndustryProfiles).Assembly;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(JsonExtension, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        var loaded = new List<(int Order, IndustryProfile Profile)>();
        foreach (var resource in resources)
        {
            var fileKey = resource.Substring(ResourcePrefix.Length, resource.Length - ResourcePrefix.Length - JsonExtension.Length);
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Industry template {resource} is missing from the build.");
            var t = JsonSerializer.Deserialize<IndustryTemplate>(stream, options)
                ?? throw new InvalidOperationException($"Industry template {resource} is empty.");
            if (!string.Equals(t.Key, fileKey, StringComparison.Ordinal))
                throw new InvalidOperationException($"Industry template {resource}: key \"{t.Key}\" must equal the file name.");
            if (loaded.Any(x => string.Equals(x.Profile.Key, t.Key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Industry template key \"{t.Key}\" is used by more than one file.");
            loaded.Add((t.Order, new IndustryProfile(t.Key, t.Title, t.Aliases, t.ProductLines, t.OrderExample, t.Tips, t.DataFocus, t.Units ?? Array.Empty<string>())));
        }
        return loaded;
    }

    /// <summary>The shape of one <c>Templates/Industries/*.json</c> file.</summary>
    internal sealed record IndustryTemplate(
        string Key,
        int Order,
        string Title,
        string Aliases,
        string[] ProductLines,
        string OrderExample,
        string[] Tips,
        string DataFocus,
        string[]? Units);
}
