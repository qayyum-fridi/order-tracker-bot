using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Words the AI router mapped to an intent (a route that parsed and ran), shared by every seller. Each word counts once per message.
/// Counts are kept in memory and saved by <see cref="FlushAsync"/> (the scheduler calls it), so they survive a restart. Words with
/// <see cref="MinVotes"/> or more votes become hints: the matcher hands them to the router as "usually means ...". Recording never
/// changes a reply, and only words leave the message (never the text, the seller or a name).
/// </summary>
public static class LearnedKeywordLog
{
    /// <summary>Votes a word needs before it is used as a hint for the router.</summary>
    public const int MinVotes = 3;
    private const int MaxStoredRows = 5000;
    private const int MaxHintRows = 2000;

    private static readonly ConcurrentDictionary<(string Word, string IntentId), int> Unsaved = new();
    private static Dictionary<string, string> _hints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts one vote per word for the intent the router chose.</summary>
    public static void Record(IEnumerable<string> words, string intentId)
    {
        foreach (var word in words)
            Unsaved.AddOrUpdate((word, intentId), 1, (_, n) => n + 1);
    }

    /// <summary>The intents the message's words are learned to mean (strongest first, no repeats). Empty when none is known yet.</summary>
    public static IReadOnlyList<string> HintsFor(IEnumerable<string> words)
    {
        var hints = _hints;
        return words.Where(hints.ContainsKey).Select(w => hints[w]).Distinct().ToList();
    }

    /// <summary>Adds the votes recorded since the last flush to the database, then reloads the hints. A failed save puts the votes back.</summary>
    public static async Task FlushAsync(IAppDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var batch = new Dictionary<(string Word, string IntentId), int>();
        foreach (var key in Unsaved.Keys)
            if (Unsaved.TryRemove(key, out var n)) batch[key] = n;

        if (batch.Count > 0)
        {
            try
            {
                var words = batch.Keys.Select(k => k.Word).Distinct().ToList();
                var rows = await db.LearnedKeywords.Where(k => words.Contains(k.Word))
                    .ToDictionaryAsync(k => (k.Word, k.IntentId), ct);
                var room = MaxStoredRows - await db.LearnedKeywords.CountAsync(ct);
                foreach (var ((word, intentId), n) in batch)
                {
                    if (rows.TryGetValue((word, intentId), out var row)) { row.Votes += n; row.LastSeenAt = nowUtc; }
                    else if (room-- > 0) db.LearnedKeywords.Add(new LearnedKeyword { Word = word, IntentId = intentId, Votes = n, FirstSeenAt = nowUtc, LastSeenAt = nowUtc });
                }
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                foreach (var (key, n) in batch) Unsaved.AddOrUpdate(key, n, (_, old) => old + n);
                throw;
            }
        }

        await RefreshHintsAsync(db, ct);
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
        _hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
