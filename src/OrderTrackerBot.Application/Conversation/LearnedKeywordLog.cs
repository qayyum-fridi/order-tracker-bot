using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Word-to-intent votes from real behaviour. A seller's vote is written when the AI router's choice ran, or when the seller taps one of the
/// options offered for an unplaced message (an explicit choice, the stronger signal). Each seller counts once per word and intent. The vote
/// is linked to the order it produced; if that order is cancelled or edited within an hour, the vote is removed (a counted vote takes one point
/// off the word). A word becomes a shared hint only after <see cref="MinVotes"/> different sellers, and <see cref="FlushAsync"/> (the scheduler)
/// adds the votes to the word counts and reloads the hints.
/// </summary>
public static class LearnedKeywordLog
{
    /// <summary>Different sellers a word needs for an intent before it is used as a hint for the router.</summary>
    public const int MinVotes = 3;
    private const int MaxStoredRows = 5000;
    private const int MaxHintRows = 2000;
    private const int CountBatch = 5000;

    private static Dictionary<string, string> _hints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds this seller's vote for each word for the intent. Returns the new votes; the turn that adds them saves them, so they can be linked to
    /// the order that turn saves (<see cref="LinkToOrder"/>). Words the seller already voted for are skipped.
    /// </summary>
    public static async Task<List<LearnedKeywordVote>> AddVotesAsync(IAppDbContext db, int sellerId, IEnumerable<string> words, string intentId, CancellationToken ct)
    {
        var list = words.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return new List<LearnedKeywordVote>();

        var voted = (await db.LearnedKeywordVotes.AsNoTracking()
                .Where(v => v.SellerId == sellerId && v.IntentId == intentId && list.Contains(v.Word))
                .Select(v => v.Word).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        voted.UnionWith(db.LearnedKeywordVotes.Local.Where(v => v.SellerId == sellerId && v.IntentId == intentId).Select(v => v.Word));

        var added = new List<LearnedKeywordVote>();
        foreach (var word in list.Where(w => !voted.Contains(w)))
        {
            var vote = new LearnedKeywordVote { Word = word, IntentId = intentId, SellerId = sellerId };
            db.LearnedKeywordVotes.Add(vote);
            added.Add(vote);
        }
        return added;
    }

    /// <summary>Links the votes of a turn to the order it saved.</summary>
    public static void LinkToOrder(IEnumerable<LearnedKeywordVote> votes, int orderId)
    {
        foreach (var vote in votes)
            if (vote.OrderId is null) vote.OrderId = orderId;
    }

    /// <summary>
    /// Removes the votes an order produced, when that order is cancelled or edited within the hour. A counted vote takes one point off its word
    /// (one seller fewer); an uncounted one is simply removed. The change is saved by the turn that calls this.
    /// </summary>
    public static async Task RetractAsync(IAppDbContext db, int sellerId, int orderId, CancellationToken ct)
    {
        var votes = await db.LearnedKeywordVotes.Where(v => v.SellerId == sellerId && v.OrderId == orderId).ToListAsync(ct);
        if (votes.Count == 0) return;

        var counted = votes.Where(v => v.Counted).ToList();
        if (counted.Count > 0)
        {
            var words = counted.Select(v => v.Word).Distinct().ToList();
            var rows = await db.LearnedKeywords.Where(k => words.Contains(k.Word)).ToListAsync(ct);
            foreach (var vote in counted)
            {
                var row = rows.FirstOrDefault(k => k.Word == vote.Word && k.IntentId == vote.IntentId);
                if (row is not null && row.Votes > 0) row.Votes--;
            }
        }
        db.LearnedKeywordVotes.RemoveRange(votes);
    }

    /// <summary>Adds the votes not yet counted to the word counts, then reloads the hints. Called by the scheduler.</summary>
    public static async Task FlushAsync(IAppDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var pending = await db.LearnedKeywordVotes.Where(v => !v.Counted).OrderBy(v => v.Id).Take(CountBatch).ToListAsync(ct);
        if (pending.Count > 0)
        {
            var words = pending.Select(v => v.Word).Distinct().ToList();
            var rows = await db.LearnedKeywords.Where(k => words.Contains(k.Word)).ToListAsync(ct);
            var room = MaxStoredRows - await db.LearnedKeywords.CountAsync(ct);
            foreach (var group in pending.GroupBy(v => (v.Word, v.IntentId)))
            {
                var row = rows.FirstOrDefault(k => k.Word == group.Key.Word && k.IntentId == group.Key.IntentId);
                if (row is null)
                {
                    if (room-- <= 0) continue;
                    row = new LearnedKeyword { Word = group.Key.Word, IntentId = group.Key.IntentId, FirstSeenAt = nowUtc, LastSeenAt = nowUtc };
                    db.LearnedKeywords.Add(row);
                    rows.Add(row);
                }
                row.Votes += group.Count();
                row.LastSeenAt = nowUtc;
            }
            foreach (var vote in pending) vote.Counted = true;
            await db.SaveChangesAsync(ct);
        }

        await RefreshHintsAsync(db, ct);
    }

    /// <summary>The intents the message's words are learned to mean (strongest first, no repeats). Empty when none is known yet.</summary>
    public static IReadOnlyList<string> HintsFor(IEnumerable<string> words)
    {
        var hints = _hints;
        return words.Where(hints.ContainsKey).Select(w => hints[w]).Distinct().ToList();
    }

    /// <summary>Reloads the hints from the stored words with enough votes. The strongest intent per word wins.</summary>
    internal static async Task RefreshHintsAsync(IAppDbContext db, CancellationToken ct)
    {
        var rows = await db.LearnedKeywords.AsNoTracking().Where(k => k.Votes >= MinVotes)
            .OrderByDescending(k => k.Votes).ThenBy(k => k.Word).Take(MaxHintRows).ToListAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) map.TryAdd(row.Word, row.IntentId);
        _hints = map;
    }

    internal static void Clear() => _hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
