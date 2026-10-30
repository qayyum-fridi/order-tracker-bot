using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Removes personal and numeric details from a message before it is stored for learning: phone numbers, amounts, and the
/// seller's own customer and product names. Output is lower-case, with {phone}, {n} and {name} in their place.
/// </summary>
public static class UtteranceMasker
{
    private const int MaxLength = 200;
    private const int MaxNames = 300;
    private static readonly Regex Phone = new(@"(?<!\d)(?:\+?92[- ]?|0)?3\d{2}[- ]?\d{7}(?!\d)|\d{10,13}", RegexOptions.Compiled);
    private static readonly Regex Amount = new(@"\d+(?:[.,]\d+)*", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static string Mask(string? text, IEnumerable<string?> knownNames)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = TextHygiene.StripInvisible(text).ToLowerInvariant();
        s = Phone.Replace(s, "{phone}");
        s = Amount.Replace(s, "{n}");
        var names = knownNames
            .Where(n => n is not null && n.Trim().Length >= 3)
            .Select(n => n!.Trim().ToLowerInvariant())
            .Distinct()
            .OrderByDescending(n => n.Length)
            .Take(MaxNames);
        foreach (var name in names)
            s = Regex.Replace(s, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])", "{name}");
        s = Spaces.Replace(s, " ").Trim();
        return s.Length > MaxLength ? s[..MaxLength] : s;
    }
}
