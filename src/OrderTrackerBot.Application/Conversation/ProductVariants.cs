using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Size and colour variants. Listings that share a name ("Polo Shirt" in white and black, or in S and M) are separate sellable items with their own
/// price and stock. A listing's identity is its name, pack, size and colour (case-insensitive). The seller names a variant with its colour or size
/// after the name ("Polo Shirt white", "Polo Shirt M", "Polo Shirt (white)"); a name that matches several variants and names none is never guessed.
/// </summary>
internal static class ProductVariants
{
    private static readonly Regex NonWords = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>"white, M", "white", "M", or "" for a plain listing.</summary>
    public static string VariantText(Product product) => string.Join(", ", new[] { product.Color, product.Size }.Where(v => !string.IsNullOrWhiteSpace(v)));

    /// <summary>The name an order keeps for a listing: "Polo Shirt (white, M)" for a variant, the plain name otherwise.</summary>
    public static string SnapshotName(Product product) => VariantText(product) is { Length: > 0 } variant ? $"{product.Name} ({variant})" : product.Name;

    /// <summary>
    /// The listing a line refers to, among the listings with its name and pack. A line that names a size or colour refers to that variant only.
    /// A line that names none refers to the plain listing, or to the only listing with that name. With several variants and no plain one it is
    /// ambiguous (<c>Ambiguous</c>) and nothing is written.
    /// </summary>
    public static (Product? Match, bool Ambiguous) MatchListing(IReadOnlyList<Product> sameName, string? size, string? color)
    {
        if (!string.IsNullOrWhiteSpace(size) || !string.IsNullOrWhiteSpace(color))
            return (sameName.FirstOrDefault(p => Same(p.Size, size) && Same(p.Color, color)), false);
        var plain = sameName.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Size) && string.IsNullOrWhiteSpace(p.Color));
        if (plain is not null) return (plain, false);
        return sameName.Count switch
        {
            0 => (null, false),
            1 => (sameName[0], false),
            _ => (null, true)
        };
    }

    /// <summary>
    /// The listings the text could mean when they are variants of one another: same name and pack, the text names no variant and no listing's label.
    /// Empty when there is nothing to choose (a single listing, or the text names one exactly).
    /// </summary>
    public static List<Product> VariantsOf(IEnumerable<Product> products, string text)
    {
        var named = products.Where(p => string.Equals(p.Name, text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count < 2 || named.Any(p => string.Equals(Formatters.ProductMatchLabel(p), text.Trim(), StringComparison.OrdinalIgnoreCase))) return new List<Product>();
        var first = named[0];
        var samePack = named.Where(p => p.UnitType == first.UnitType && p.UnitQty == first.UnitQty).ToList();
        return samePack.Count > 1 ? samePack : new List<Product>();
    }

    /// <summary>The one listing the text names with its name and a variant word ("Polo Shirt white", "polo shirt m"); null when none or several.</summary>
    public static Product? Find(IEnumerable<Product> products, string text)
    {
        var hits = products.Where(p => NamesVariant(p, text)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>True when the text is this listing's name followed by its colour and/or size, in either order.</summary>
    public static bool NamesVariant(Product product, string text)
    {
        var name = Words(product.Name);
        var wanted = Words(text);
        if (!wanted.StartsWith(name + " ", StringComparison.Ordinal)) return false;
        var rest = wanted[(name.Length + 1)..];
        var color = Words(product.Color ?? "");
        var size = Words(product.Size ?? "");
        if (rest.Length == 0) return false;
        return (color.Length > 0 && rest == color) || (size.Length > 0 && rest == size)
            || (color.Length > 0 && size.Length > 0 && (rest == $"{color} {size}" || rest == $"{size} {color}"));
    }

    private static bool Same(string? a, string? b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Lower case, punctuation as spaces: "Polo Shirt (White)" and "polo shirt white" compare equal.</summary>
    private static string Words(string value) => NonWords.Replace(value.ToLowerInvariant(), " ").Trim();
}
