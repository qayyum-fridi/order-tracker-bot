using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Intents;

/// <summary>
/// Plain word matching against the intent keywords and stop words, both read at runtime from <see cref="KeywordStore"/> (database rows,
/// seeded from <c>intents.json</c> and <c>keywords.seed.json</c>). A word only counts when it is a whole word of the message, so "order"
/// does not match "orders". Used before the AI router: a message that only one intent claims is routed without a model call
/// (see <c>ConversationEngine.DirectKeywordRoute</c>).
/// </summary>
public static class IntentKeywords
{
    private const int MinWordLength = 3;
    private const int MaxWordLength = 30;
    private static readonly Regex Split = new(@"[^\p{L}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The words of a message, lower-cased, without stop words and without anything too short or too long.</summary>
    public static HashSet<string> Tokens(string? text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Split.Split(text ?? ""))
        {
            var word = part.ToLowerInvariant();
            if (word.Length is < MinWordLength or > MaxWordLength || KeywordStore.IsStopWord(word)) continue;
            words.Add(word);
        }
        return words;
    }

    /// <summary>The intents whose keywords appear in the message, in catalog order. A keyword with a space must appear as those words in a row.</summary>
    public static IReadOnlyList<string> Match(string? text, IEnumerable<IntentDefinition> intents)
    {
        var tokens = Tokens(text);
        var spaced = " " + string.Join(" ", Split.Split((text ?? "").ToLowerInvariant()).Where(p => p.Length > 0)) + " ";
        return intents
            .Where(intent => KeywordsOf(intent).Any(k => k.Contains(' ')
                ? spaced.Contains(" " + k.Trim() + " ", StringComparison.OrdinalIgnoreCase)
                : tokens.Contains(k)))
            .Select(intent => intent.Id)
            .ToList();
    }

    /// <summary>
    /// Single-word keywords matched with a plural or singular form ("orders" for "order", "customers" for "customer"). Only a fallback for
    /// <see cref="Match"/>: exact matches always win, so a plural never changes what an exact word already routes.
    /// </summary>
    public static IReadOnlyList<string> MatchPlural(string? text, IEnumerable<IntentDefinition> intents)
    {
        var tokens = Tokens(text);
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens)
        {
            forms.Add(t);
            if (t.EndsWith("es", StringComparison.Ordinal)) forms.Add(t[..^2]);
            if (t.EndsWith('s')) forms.Add(t[..^1]);
            forms.Add(t + "s");
        }
        return intents
            .Where(intent => KeywordsOf(intent).Any(k => !k.Contains(' ') && forms.Contains(k)))
            .Select(intent => intent.Id)
            .ToList();
    }

    /// <summary>
    /// <see cref="Match"/>, plus intents with a single-word keyword the message spells nearly: same first two letters, one edit or the same consonants, four letters or
    /// more ("kasa" for "kaise"). Used for how-to words, where a typo should not hide the question; a wrong catch only costs a how-to answer.
    /// </summary>
    public static IReadOnlyList<string> MatchTolerant(string? text, IEnumerable<IntentDefinition> intents)
    {
        var tokens = Tokens(text);
        var exact = Match(text, intents);
        return intents
            .Where(intent => exact.Contains(intent.Id) || KeywordsOf(intent).Any(k => !k.Contains(' ') && tokens.Any(t => IsNear(t, k))))
            .Select(intent => intent.Id)
            .ToList();
    }

    /// <summary>True when <paramref name="token"/> is a near spelling of <paramref name="keyword"/> (not the same word).</summary>
    public static bool IsNear(string token, string keyword) =>
        token.Length >= 4 && keyword.Length >= 4 && !string.Equals(token, keyword, StringComparison.OrdinalIgnoreCase)
        && string.Compare(token, 0, keyword, 0, 2, StringComparison.OrdinalIgnoreCase) == 0
        && (Conversation.TextDistance.Levenshtein(token, keyword) <= 1 || Skeleton(token) == Skeleton(keyword));

    // Roman Urdu vowels move around ("kasa" / "kaise" / "kese"): the same consonants in the same order is the same word.
    private static string Skeleton(string word) => new(word.ToLowerInvariant().Where(c => "aeiouy".IndexOf(c) < 0).ToArray());

    /// <summary>The intents (among those given) that the message matches through a multi-word keyword, which is more specific than a single word.</summary>
    public static IReadOnlyList<string> MatchPhrase(string? text, IEnumerable<IntentDefinition> intents)
    {
        var spaced = " " + string.Join(" ", Split.Split((text ?? "").ToLowerInvariant()).Where(p => p.Length > 0)) + " ";
        return intents
            .Where(intent => KeywordsOf(intent).Any(k => k.Contains(' ') && spaced.Contains(" " + k.Trim() + " ", StringComparison.OrdinalIgnoreCase)))
            .Select(intent => intent.Id)
            .ToList();
    }

    /// <summary>The runtime keywords of an intent (database), or the catalog keywords when the database has none for it.</summary>
    private static IEnumerable<string> KeywordsOf(IntentDefinition intent) =>
        KeywordStore.IntentWords(intent.Id) ?? (IEnumerable<string>?)intent.Keywords ?? Array.Empty<string>();
}
