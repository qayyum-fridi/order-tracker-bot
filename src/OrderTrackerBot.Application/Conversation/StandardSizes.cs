namespace OrderTrackerBot.Application.Conversation;

// The standard size list a product can come in. A size the model reads in another spelling ("Small", "Double XL") is mapped here;
// anything else is dropped rather than saved as a guess.
public static class StandardSizes
{
    public static readonly IReadOnlyList<string> All = new[] { "XXS", "XS", "S", "M", "L", "XL", "XXL", "3XL", "4XL", "Free Size" };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["extra small"] = "XS", ["small"] = "S", ["medium"] = "M", ["large"] = "L", ["extra large"] = "XL",
        ["double xl"] = "XXL", ["2xl"] = "XXL", ["xxxl"] = "3XL", ["xxxxl"] = "4XL",
        ["free"] = "Free Size", ["freesize"] = "Free Size", ["one size"] = "Free Size",
    };

    public static string? Normalize(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        var standard = All.FirstOrDefault(s => string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase));
        if (standard is not null) return standard;
        return Aliases.TryGetValue(trimmed, out var alias) ? alias : null;
    }

    public static List<string> NormalizeAll(IEnumerable<string> raw) =>
        raw.Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
