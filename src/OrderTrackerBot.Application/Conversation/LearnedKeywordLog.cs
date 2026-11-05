using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Words the AI router mapped to an intent (a route that parsed and ran). Each seller votes once per word and intent, so a word becomes a shared
/// hint only after <see cref="MinVotes"/> different sellers used it; one seller repeating a word never does. Votes are kept in memory and saved by
/// <see cref="FlushAsync"/> (the scheduler calls it), so they survive a restart. A batch that keeps failing to save is dropped after
/// <see cref="MaxFailedFlushes"/> tries, so one bad batch cannot block the log forever. Recording never changes a reply.
/// </summary>
public static class LearnedKeywordLog
{
    /// <summary>Different sellers a word needs for an intent before it is used as a hint for the router.</summary>
    public const int MinVotes = 3;
    private const int MaxStoredRows = 5000;
    private const int MaxHintRows = 2000;
    private const int MaxFailedFlushes = 3;

    private static readonly ConcurrentDictionary<(string Word, string IntentId, int SellerId), byte> Unsaved = new();
    private static Dictionary<string, string> _hints = new(StringComparer.OrdinalIgnoreCase);
    private static int _failedFlushes;

    /// <summary>Records one vote per word for the intent the router chose, from this seller.</summary>
    public static void Record(IEnumerable<string> words, string intentId, int sellerId)
    {
        foreach (var word in words)
            Unsaved.TryAdd((word, intentId, sellerId), 0);
    }

    /// <summary>The intents the message's words are learned to mean (strongest first, no repeats). Empty when none is known yet.</summary>
    public static IReadOnlyList<string> HintsFor(IEnumerable<string> words)
    {
        var hints = _hints;
        return words.Where(hints.ContainsKey).Select(w => hints[w]).Distinct().ToList();
    }

    /// <summary>Saves the votes recorded since the last flush, then reloads the hints. A failed save puts them back; after <see cref="MaxFailedFlushes"/> failures the batch is dropped.</summary>
    public static async Task FlushAsync(IAppDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var batch = new List<(string Word, string IntentId, int SellerId)>();
        foreach (var key in Unsaved.Keys)
            if (Unsaved.TryRemove(key, out _)) batch.Add(key);

        if (batch.Count > 0)
        {
            try
            {
                await SaveVotesAsync(db, batch, nowUtc, ct);
                _failedFlushes = 0;
            }
            catch
            {
                if (++_failedFlushes >= MaxFailedFlushes)
                {
                    _failedFlushes = 0;   // give up on this batch: a bad word must not block the log forever
                    return;
                }
                foreach (var key in batch) Unsaved.TryAdd(key, 0);
                throw;
            }
        }

        await RefreshHintsAsync(db, ct);
    }

    private static async Task SaveVotesAsync(IAppDbContext db, List<(string Word, string IntentId, int SellerId)> batch, DateTime nowUtc, CancellationToken ct)
    {
        var words = batch.Select(b => b.Word).Distinct().ToList();
        var seen = (await db.LearnedKeywordVotes.AsNoTracking().Where(v => words.Contains(v.Word))
            .Select(v => new { v.Word, v.IntentId, v.SellerId }).ToListAsync(ct))
            .Select(v => (v.Word, v.IntentId, v.SellerId)).ToHashSet();
        var fresh = batch.Where(b => !seen.Contains(b)).Distinct().ToList();
        if (fresh.Count == 0) return;

        var keywords = await db.LearnedKeywords.Where(k => words.Contains(k.Word)).ToListAsync(ct);
        var room = MaxStoredRows - await db.LearnedKeywords.CountAsync(ct);
        foreach (var group in fresh.GroupBy(b => (b.Word, b.IntentId)))
        {
            var row = keywords.FirstOrDefault(k => k.Word == group.Key.Word && k.IntentId == group.Key.IntentId);
            if (row is null)
            {
                if (room-- <= 0) continue;
                row = new LearnedKeyword { Word = group.Key.Word, IntentId = group.Key.IntentId, FirstSeenAt = nowUtc, LastSeenAt = nowUtc };
                db.LearnedKeywords.Add(row);
                keywords.Add(row);
            }
            row.Votes += group.Count();
            row.LastSeenAt = nowUtc;
        }
        foreach (var vote in fresh)
            db.LearnedKeywordVotes.Add(new LearnedKeywordVote { Word = vote.Word, IntentId = vote.IntentId, SellerId = vote.SellerId, CreatedAt = nowUtc });
        await db.SaveChangesAsync(ct);
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

    internal static void Clear()
    {
        Unsaved.Clear();
        _failedFlushes = 0;
        _hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
