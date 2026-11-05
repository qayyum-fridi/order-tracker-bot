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
