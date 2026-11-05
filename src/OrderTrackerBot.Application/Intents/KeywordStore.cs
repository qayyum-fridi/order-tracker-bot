using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Intents;

/// <summary>
/// The word lists the matcher uses at runtime: stop words and intent keywords, held in <see cref="KeywordRule"/> rows so they can change
/// without a build. The rows are seeded from <c>Intents/keywords.seed.json</c> and <c>intents.json</c> (missing rows only; an existing
/// row is never overwritten, a disabled one stays disabled). Until the first <see cref="SyncAsync"/> the seed values are used, so the
/// matcher works without a database.
/// </summary>
public static class KeywordStore
{
    private const string SeedResource = "Intents.keywords.seed.json";
    private static Snapshot _current = Snapshot.FromSeed();

    /// <summary>Stop words: ignored when a message is read for keywords.</summary>
    public static bool IsStopWord(string word) => _current.StopWords.Contains(word);

    /// <summary>The active keywords of an intent when the database has rows for it; null when it has none (the catalog keywords are used).</summary>
    public static IReadOnlyCollection<string>? IntentWords(string intentId) =>
        _current.IntentWords.TryGetValue(intentId, out var words) ? words : null;

    /// <summary>Adds the seed rows that are missing from the database, then reloads the lists from the rows. Safe to call on every tick.</summary>
    public static async Task SyncAsync(IAppDbContext db, CancellationToken ct)
    {
        var existing = (await db.KeywordRules.AsNoTracking().ToListAsync(ct))
            .Select(r => (Word: r.Word.ToLowerInvariant(), r.IntentId))
            .ToHashSet();

        var added = false;
        foreach (var word in SeedStopWords())
            if (existing.Add((word, null)))
            {
                db.KeywordRules.Add(new KeywordRule { Word = word });
                added = true;
            }
        foreach (var intent in IntentCatalog.All)
            foreach (var word in intent.Keywords ?? Array.Empty<string>())
                if (existing.Add((word.ToLowerInvariant(), intent.Id)))
                {
                    db.KeywordRules.Add(new KeywordRule { Word = word.ToLowerInvariant(), IntentId = intent.Id });
                    added = true;
                }
        if (added) await db.SaveChangesAsync(ct);

        var rows = await db.KeywordRules.AsNoTracking().ToListAsync(ct);
        _current = Snapshot.FromRows(rows);
    }

    /// <summary>Back to the seed values (tests).</summary>
    internal static void Reset() => _current = Snapshot.FromSeed();

    private static IEnumerable<string> SeedStopWords()
    {
        using var stream = typeof(KeywordStore).Assembly.GetManifestResourceStream(SeedResource)
            ?? throw new InvalidOperationException($"{SeedResource} is not embedded in the Application assembly.");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("stopWords").EnumerateArray()
            .Select(w => w.GetString()!.ToLowerInvariant()).ToList();
    }

    private sealed class Snapshot
    {
        public required HashSet<string> StopWords { get; init; }
        public required Dictionary<string, IReadOnlyCollection<string>> IntentWords { get; init; }

        public static Snapshot FromSeed() => new()
        {
            StopWords = SeedStopWords().ToHashSet(StringComparer.OrdinalIgnoreCase),
            IntentWords = IntentCatalog.All.ToDictionary(
                i => i.Id,
                i => (IReadOnlyCollection<string>)(i.Keywords ?? Array.Empty<string>()).Select(k => k.ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal),
        };

        public static Snapshot FromRows(IEnumerable<KeywordRule> rows)
        {
            var list = rows.ToList();
            var stop = list.Where(r => r.IntentId is null && r.Active).Select(r => r.Word.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var intents = list.Where(r => r.IntentId is not null).GroupBy(r => r.IntentId!, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyCollection<string>)g.Where(r => r.Active).Select(r => r.Word.ToLowerInvariant())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.Ordinal);
            return new Snapshot { StopWords = stop, IntentWords = intents };
        }
    }
}
