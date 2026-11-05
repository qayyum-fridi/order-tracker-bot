using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// "Did you mean": an unplaced message with exactly one word one or two letters away from a known keyword gets a question with two taps.
/// Yes runs the corrected message through the normal path and votes the typed word for the keyword's intent (when only one intent has it);
/// No sends the usual options; any other reply drops the question. A message with several such words, or a word equally close to two
/// keywords, gets no question: it is not a clear typo.
/// </summary>
public partial class ConversationEngine
{
    private static readonly TimeSpan WordFixWindow = TimeSpan.FromMinutes(10);
    // Button labels the engine matches on stay in code (the translation layer maps them back).
    internal const string WordFixYes = "✅ Haan, sahi hai";
    internal const string WordFixNo = "❌ Nahi";

    /// <summary>A single misspelt keyword: the typed word, the keyword it means, the message with that word corrected, and the intent (null when unclear).</summary>
    internal sealed record WordFixCandidate(string From, string To, string Corrected, string? Intent);

    /// <summary>The one misspelt keyword in the message, or null when there is not exactly one. Names of the seller (<paramref name="protectedWords"/>) are never corrected.</summary>
    internal static WordFixCandidate? FindOneWordFix(string message, IReadOnlyList<(string Word, string IntentId)> vocabulary, ISet<string> protectedWords)
    {
        var known = vocabulary.Select(v => v.Word).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fixes = new List<(string From, string To, string? Intent)>();
        foreach (Match m in Regex.Matches(message, @"\p{L}{4,}"))
        {
            var word = m.Value.ToLowerInvariant();
            if (known.Contains(word) || protectedWords.Contains(word) || KeywordStore.IsStopWord(word) || KeywordStore.IsGenericMissWord(word)) continue;

            var limit = word.Length >= 6 ? 2 : 1;
            var close = vocabulary.Where(v => v.Word.Length >= 4)
                .Select(v => (v.Word, v.IntentId, Distance: TextDistance.Levenshtein(word, v.Word)))
                .Where(x => x.Distance <= limit).ToList();
            if (close.Count == 0) continue;

            var best = close.Min(x => x.Distance);
            var targets = close.Where(x => x.Distance == best).Select(x => x.Word).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (targets.Count != 1) continue;   // two keywords equally close: not a clear typo

            var intents = vocabulary.Where(v => string.Equals(v.Word, targets[0], StringComparison.OrdinalIgnoreCase)).Select(v => v.IntentId).Distinct().ToList();
            fixes.Add((m.Value, targets[0], intents.Count == 1 ? intents[0] : null));
        }
        if (fixes.Count != 1) return null;

        var (from, to, intent) = fixes[0];
        var corrected = Regex.Replace(message, @"(?<!\p{L})" + Regex.Escape(from) + @"(?!\p{L})", to, RegexOptions.IgnoreCase);
        return new WordFixCandidate(from, to, corrected, intent);
    }

    /// <summary>Asks "did you mean" for the one misspelt keyword in an unplaced message. False (nothing sent) when there is no clear typo.</summary>
    private async Task<bool> TrySuggestWordFixAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var names = (await _db.Products.Where(p => p.SellerId == seller.Id).Select(p => p.Name).Take(500).ToListAsync(ct))
            .Concat(await _db.Customers.Where(c => c.SellerId == seller.Id).Select(c => c.Name).Take(500).ToListAsync(ct));
        var protectedWords = names.SelectMany(IntentKeywords.Tokens).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = FindOneWordFix(message, KeywordStore.IntentVocabulary(), protectedWords);
        if (candidate is null) return false;

        ctx.PendingWordFix = new PendingWordFixData
        {
            Original = message, From = candidate.From, To = candidate.To, Corrected = candidate.Corrected,
            Intent = candidate.Intent, At = _clock.GetUtcNow().UtcDateTime,
        };
        await PersistAsync(session, ctx, ct);
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("word-fix.001", candidate.From, candidate.To),
            new List<ChoiceOption> { new(WordFixYes, WordFixYes), new(WordFixNo, WordFixNo) }, ct);
        return true;
    }

    /// <summary>
    /// The router was not sure, but it named one command: the seller is asked "do you mean ...?" with Yes / No taps. Yes runs that command
    /// (and learns the message's words for its intent); No falls through to the keyword question and the usual options.
    /// </summary>
    private async Task<bool> TrySuggestAiGuessAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (_turnGuess is not { } guess) return false;
        ctx.PendingWordFix = new PendingWordFixData
        {
            Original = message, From = "", To = guess.Command, Corrected = guess.Command,
            Intent = guess.IntentId, At = _clock.GetUtcNow().UtcDateTime,
        };
        await PersistAsync(session, ctx, ct);
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("ai-guess.001", guess.Command),
            new List<ChoiceOption> { new(WordFixYes, WordFixYes), new(WordFixNo, WordFixNo) }, ct);
        return true;
    }

    /// <summary>
    /// Reads the answer to a "did you mean" question. Returns (true, corrected message) for Yes: the caller runs the corrected message;
    /// (true, null) for No: the usual options were sent; (false, null) when there is no question or the reply is something else.
    /// </summary>
    private async Task<(bool Answered, string? Corrected)> TakeWordFixAnswerAsync(string fromPhoneNumber, string rawMessage, CancellationToken ct)
    {
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        if (seller.Session is not { } session) return (false, null);
        var ctx = SessionContextData.FromJson(session.ContextJson);
        if (ctx.PendingWordFix is not { } fix) return (false, null);
        ctx.PendingWordFix = null;
        await PersistAsync(session, ctx, ct);

        var text = rawMessage.Trim();
        if (_clock.GetUtcNow().UtcDateTime - fix.At > WordFixWindow) return (false, null);
        if (text == WordFixYes || CommandParser.IsAffirmative(text))
        {
            if (fix.Intent is not null)
            {
                _turnRouteIntent = fix.Intent;
                _turnVotes.AddRange(await LearnedKeywordLog.AddVotesAsync(_db, seller.Id, new[] { fix.From.ToLowerInvariant() }, fix.Intent, ct));
            }
            return (true, fix.Corrected);
        }
        if (text == WordFixNo || CommandParser.IsNegative(text))
        {
            await TrySendMissChoicesAsync(seller, session, ctx, fix.Original, ct);
            return (true, null);
        }
        return (false, null);
    }
}

/// <summary>The "did you mean" question waiting for an answer: the original message, the typed word, the suggestion and when it was asked.</summary>
public sealed class PendingWordFixData
{
    public string Original { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Corrected { get; set; } = "";
    public string? Intent { get; set; }
    public DateTime At { get; set; }
}
