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

    // A word that appears in more intents than this is too common to point at one action.
    private const int MaxIntentsPerMissWord = 8;

    /// <summary>The catalog examples nearest to the message: each option's label is the text the seller would send, so a tap runs it.</summary>
    internal static IReadOnlyList<ChoiceOption> MissChoiceOptions(string message)
    {
        // Generic words (dikhao, kitna, add: the runtime list in KeywordStore) never decide which options fit a message.
        var mine = PhraseTokens(message).Where(t => t.Length >= 3 && !KeywordStore.IsGenericMissWord(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        await RememberMissChoicesAsync(seller, message, options, ct);
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("miss-choices.001"), options, ct);
        return true;
    }

    /// <summary>How long a tap on the offered options still counts as an answer to the unplaced message.</summary>
    private static readonly TimeSpan MissChoiceWindow = TimeSpan.FromMinutes(10);

    /// <summary>Keeps the words of the unplaced message and the intent of each option, so a tap can vote them for that intent.</summary>
    private async Task RememberMissChoicesAsync(Seller seller, string message, IReadOnlyList<ChoiceOption> options, CancellationToken ct)
    {
        if (seller.Session is not { } session) return;
        var words = await OwnFilteredWordsAsync(seller, IntentKeywords.Tokens(message), ct);
        words.RemoveWhere(KeywordStore.IsGenericMissWord);
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.MissChoiceWords = words.ToList();
        ctx.MissChoiceIntents = options
            .Where(o => IntentFor(o.Value) is not null)
            .ToDictionary(o => o.Value, o => IntentFor(o.Value)!, StringComparer.OrdinalIgnoreCase);
        ctx.MissChoiceAt = _clock.GetUtcNow().UtcDateTime;
        await PersistAsync(session, ctx, ct);
    }

    /// <summary>The intent whose catalog examples include this option text (the first in catalog order).</summary>
    private static string? IntentFor(string example) =>
        IntentCatalog.All.FirstOrDefault(i => i.Examples.Contains(example, StringComparer.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// A tap on one of the options offered for an unplaced message is an explicit choice: the words of that message vote for the tapped intent
    /// (within ten minutes). Any other message clears the offer.
    /// </summary>
    private async Task LearnFromMissTapAsync(string fromPhoneNumber, string rawMessage, CancellationToken ct)
    {
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        if (seller.Session is not { } session) return;
        var ctx = SessionContextData.FromJson(session.ContextJson);
        if (ctx.MissChoiceIntents is not { Count: > 0 } offered) return;

        var words = ctx.MissChoiceWords ?? new List<string>();
        var fresh = ctx.MissChoiceAt is { } at && _clock.GetUtcNow().UtcDateTime - at <= MissChoiceWindow;
        var tapped = offered.TryGetValue(rawMessage.Trim(), out var intentId) ? intentId : null;
        ctx.MissChoiceIntents = null;
        ctx.MissChoiceWords = null;
        ctx.MissChoiceAt = null;
        if (tapped is not null && fresh)
        {
            _turnRouteIntent = tapped;
            _turnVotes.AddRange(await LearnedKeywordLog.AddVotesAsync(_db, seller.Id, words, tapped, ct));
        }
        await PersistAsync(session, ctx, ct);
    }
}
