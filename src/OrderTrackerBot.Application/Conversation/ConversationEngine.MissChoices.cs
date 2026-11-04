using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// When an idle text message is not placed by the parser, the AI route (if any) or the intent catalog, the seller gets up to three
// tappable examples of what they could mean, instead of only "samajh nahi aaya". Deterministic: the options are catalog examples that
// already parse to their own action, shared words pick them, and risky actions are never offered as a tap.
public partial class ConversationEngine
{
    private const int MaxMissChoices = 3;
    private const int MaxMissLabel = 20;

    // Words that every command shares ("dikhao", "kitna", "add"): they never decide which options fit a message.
    private static readonly HashSet<string> GenericMissWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dikhao", "dikha", "batao", "bata", "karo", "kar", "kardo", "badlo", "badal", "hai", "hua", "hain", "kya", "kitna", "kitni", "kitne",
        "mein", "wale", "wala", "list", "add", "para", "dena", "dedo", "chahiye", "number", "wali", "sab", "tak", "abhi", "aaj", "kal", "kaise",
        "dekho", "dekhna", "bhejo", "bhej", "kahan", "kab", "kaun", "kuch", "aur", "naya", "nayi", "daal", "likho", "likhna"
    };

    // A word that appears in more intents than this is too common to point at one action.
    private const int MaxIntentsPerMissWord = 8;

    /// <summary>The catalog examples nearest to the message: each option's label is the text the seller would send, so a tap runs it.</summary>
    internal static IReadOnlyList<ChoiceOption> MissChoiceOptions(string message)
    {
        var mine = PhraseTokens(message).Where(t => t.Length >= 3 && !GenericMissWords.Contains(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mine.Count == 0) return Array.Empty<ChoiceOption>();

        var wordsPerIntent = IntentCatalog.All.Select(intent =>
            PhraseTokens(intent.Description + " " + string.Join(" ", intent.Examples)).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToList();
        var intentsWithWord = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in wordsPerIntent.SelectMany(w => w)) intentsWithWord[word] = intentsWithWord.GetValueOrDefault(word) + 1;

        var ranked = new List<(int Score, int Index, ChoiceOption Option)>();
        for (var i = 0; i < IntentCatalog.All.Count; i++)
        {
            var example = SafeExampleFor(IntentCatalog.All[i]);
            if (example is null) continue;
            var shared = mine.Count(t => wordsPerIntent[i].Contains(t) && intentsWithWord.GetValueOrDefault(t) <= MaxIntentsPerMissWord);
            if (shared == 0) continue;
            ranked.Add((shared, i, new ChoiceOption(example, example)));
        }

        return ranked.OrderByDescending(r => r.Score).ThenBy(r => r.Index)
            .Select(r => r.Option)
            .GroupBy(o => o.Value, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Take(MaxMissChoices).ToList();
    }

    /// <summary>The first example of an intent that is short enough for a button, parses to that intent's own action and is not risky; null when none is.</summary>
    private static string? SafeExampleFor(IntentDefinition intent)
    {
        if (!Enum.TryParse<CommandKind>(intent.Kind, out var kind) || ActionRisk.RiskyCommands.Contains(kind)) return null;
        foreach (var example in intent.Examples)
        {
            if (example.Length == 0 || example.Length > MaxMissLabel) continue;
            if (CommandParser.TryParse(example) is { } parsed && parsed.Kind == kind && !ActionRisk.RiskyCommands.Contains(parsed.Kind)) return example;
        }
        return null;
    }

    /// <summary>Sends the tap options for an unplaced idle message. False (nothing sent) when no catalog example is close enough.</summary>
    private async Task<bool> TrySendMissChoicesAsync(Seller seller, string message, CancellationToken ct)
    {
        var options = MissChoiceOptions(message);
        if (options.Count == 0) return false;
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("miss-choices.001"), options, ct);
        return true;
    }
}
