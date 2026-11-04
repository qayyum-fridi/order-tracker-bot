using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Counts the number words a voice note used that <see cref="SpokenNumbers"/> could not read (the word only, never the transcript or the seller),
/// so the spelling list can grow from real use. Counts are kept in memory and saved to the database by <see cref="FlushAsync"/>
/// (the scheduler calls it), so they survive a restart. Recording never touches the database and changes no reply.
/// </summary>
public static class UnknownNumberWordLog
{
    private const int MaxDistinctWords = 500;
    private const int MaxStoredWords = 2000;
    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> Unsaved = new(StringComparer.OrdinalIgnoreCase);

    public static void Record(IEnumerable<string> words)
    {
        foreach (var raw in words)
        {
            var word = raw.Trim().ToLowerInvariant();
            if (word.Length is < 3 or > 30 || !word.All(char.IsLetter)) continue;
            if (!Counts.ContainsKey(word) && Counts.Count >= MaxDistinctWords) continue;
            Counts.AddOrUpdate(word, 1, (_, n) => n + 1);
            Unsaved.AddOrUpdate(word, 1, (_, n) => n + 1);
        }
    }

    /// <summary>Since the last restart, most frequent first.</summary>
    public static IReadOnlyList<(string Word, int Count)> Snapshot() =>
        Counts.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();

    /// <summary>Saved counts, most frequent first (every restart included).</summary>
    public static async Task<IReadOnlyList<(string Word, int Count)>> StoredAsync(IAppDbContext db, CancellationToken ct) =>
        (await db.UnknownNumberWords.AsNoTracking().OrderByDescending(w => w.Count).ThenBy(w => w.Word).Take(200).ToListAsync(ct))
        .Select(w => (w.Word, w.Count)).ToList();

    /// <summary>Adds the counts recorded since the last flush to the database. A failed save puts them back, so nothing is lost.</summary>
    public static async Task FlushAsync(IAppDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var batch = new Dictionary<string, int>();
        foreach (var key in Unsaved.Keys)
            if (Unsaved.TryRemove(key, out var n)) batch[key] = n;
        if (batch.Count == 0) return;

        try
        {
            var words = batch.Keys.ToList();
            var rows = await db.UnknownNumberWords.Where(w => words.Contains(w.Word)).ToDictionaryAsync(w => w.Word, StringComparer.OrdinalIgnoreCase, ct);
            var room = MaxStoredWords - await db.UnknownNumberWords.CountAsync(ct);
            foreach (var (word, n) in batch)
            {
                if (rows.TryGetValue(word, out var row)) { row.Count += n; row.LastSeenAt = nowUtc; }
                else if (room-- > 0) db.UnknownNumberWords.Add(new UnknownNumberWord { Word = word, Count = n, FirstSeenAt = nowUtc, LastSeenAt = nowUtc });
            }
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            foreach (var (word, n) in batch) Unsaved.AddOrUpdate(word, n, (_, old) => old + n);
            throw;
        }
    }

    internal static void Clear() { Counts.Clear(); Unsaved.Clear(); }
}
