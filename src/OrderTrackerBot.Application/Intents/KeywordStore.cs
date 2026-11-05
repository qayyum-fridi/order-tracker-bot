using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Intents;

/// <summary>A feature area: when a message matches <see cref="Keywords"/>, the seller is offered <see cref="Options"/> (typed commands).</summary>
public sealed record CommandArea(string Question, Regex Keywords, string[] Options);

/// <summary>
/// The word lists and feature areas the bot matches on at runtime. Stop words, generic miss words and intent keywords are <see cref="KeywordRule"/>
/// rows (the group is <see cref="KeywordRule.IntentId"/>: null = stop word, <see cref="GenericGroup"/> = generic miss word, otherwise the intent id);
/// the feature areas are <see cref="FeatureAreaRow"/> rows. All are seeded once from the embedded JSON files (missing rows only; an existing row is
/// never overwritten, a disabled one stays disabled; a feature area is keyed by its question). Until the first <see cref="SyncAsync"/> the seed
/// values are used, so the bot works without a database. A feature area whose pattern does not compile is skipped.
/// </summary>
public static class KeywordStore
{
    /// <summary><see cref="KeywordRule.IntentId"/> of a generic miss word (a word every command shares, never decides which options fit).</summary>
    public const string GenericGroup = "generic";

    private const string KeywordSeedResource = "Intents.keywords.seed.json";
    private const string AreaSeedResource = "Intents.feature-areas.seed.json";
    private const RegexOptions AreaOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static Snapshot _current = Snapshot.FromSeed();

    /// <summary>Stop words: ignored when a message is read for keywords.</summary>
    public static bool IsStopWord(string word) => _current.StopWords.Contains(word);

    /// <summary>Generic miss words: never used to pick the options offered for an unplaced message.</summary>
    public static bool IsGenericMissWord(string word) => _current.GenericWords.Contains(word);

    /// <summary>The active keywords of an intent when the database has rows for it; null when it has none (the catalog keywords are used).</summary>
    public static IReadOnlyCollection<string>? IntentWords(string intentId) =>
        _current.IntentWords.TryGetValue(intentId, out var words) ? words : null;

    /// <summary>Every intent keyword in use, with the intent it belongs to (the vocabulary a misspelt word is compared with).</summary>
    public static IReadOnlyList<(string Word, string IntentId)> IntentVocabulary() =>
        _current.IntentWords.SelectMany(p => p.Value.Select(w => (w, p.Key))).ToList();

    /// <summary>The feature areas in use, in order: the first whose keywords match a message decides the question.</summary>
    public static IReadOnlyList<CommandArea> CommandAreas => _current.Areas;

    /// <summary>Adds the seed rows that are missing from the database, then reloads the lists from the rows. Safe to call on every tick.</summary>
    public static async Task SyncAsync(IAppDbContext db, CancellationToken ct)
    {
        var existing = (await db.KeywordRules.AsNoTracking().ToListAsync(ct))
            .Select(r => (Word: r.Word.ToLowerInvariant(), r.IntentId))
            .ToHashSet();

        var added = false;
        foreach (var word in SeedWords("stopWords"))
            if (existing.Add((word, null)))
            {
                db.KeywordRules.Add(new KeywordRule { Word = word });
                added = true;
            }
        foreach (var word in SeedWords("genericWords"))
            if (existing.Add((word, GenericGroup)))
            {
                db.KeywordRules.Add(new KeywordRule { Word = word, IntentId = GenericGroup });
                added = true;
            }
        foreach (var intent in IntentCatalog.All)
            foreach (var word in intent.Keywords ?? Array.Empty<string>())
                if (existing.Add((word.ToLowerInvariant(), intent.Id)))
                {
                    db.KeywordRules.Add(new KeywordRule { Word = word.ToLowerInvariant(), IntentId = intent.Id });
                    added = true;
                }

        // A shipped area that changed follows into rows nobody edited (their stored seed hash still matches their text); edited rows are left alone.
        var areaByQuestion = (await db.FeatureAreas.ToListAsync(ct)).ToDictionary(a => a.Question, StringComparer.Ordinal);
        foreach (var (question, pattern, options) in SeedAreas())
        {
            var optionsJson = JsonSerializer.Serialize(options);
            var shipped = SeedHash.Of(pattern, optionsJson);
            if (!areaByQuestion.TryGetValue(question, out var area))
            {
                db.FeatureAreas.Add(new FeatureAreaRow { Question = question, Pattern = pattern, Options = optionsJson, SeedHash = shipped });
                added = true;
                continue;
            }
            switch (SeedHash.Decide(area.SeedHash, SeedHash.Of(area.Pattern, area.Options), shipped))
            {
                case SeedAction.Stamp:
                    area.SeedHash = shipped;
                    added = true;
                    break;
                case SeedAction.Update:
                    (area.Pattern, area.Options, area.SeedHash) = (pattern, optionsJson, shipped);
                    added = true;
                    break;
            }
        }
        if (added) await db.SaveChangesAsync(ct);

        var rows = await db.KeywordRules.AsNoTracking().ToListAsync(ct);
        var areaRows = await db.FeatureAreas.AsNoTracking().ToListAsync(ct);
        _current = Snapshot.FromRows(rows, areaRows);
    }

    /// <summary>Back to the seed values (tests).</summary>
    internal static void Reset() => _current = Snapshot.FromSeed();

    /// <summary>The words of one list of the keyword seed file ("stopWords", "genericWords"), lower-cased.</summary>
    private static IEnumerable<string> SeedWords(string property)
    {
        using var stream = typeof(KeywordStore).Assembly.GetManifestResourceStream(KeywordSeedResource)
            ?? throw new InvalidOperationException($"{KeywordSeedResource} is not embedded in the Application assembly.");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty(property).EnumerateArray()
            .Select(w => w.GetString()!.ToLowerInvariant()).ToList();
    }

    /// <summary>The shipped feature areas: question, pattern and options.</summary>
    private static IEnumerable<(string Question, string Pattern, string[] Options)> SeedAreas()
    {
        using var stream = typeof(KeywordStore).Assembly.GetManifestResourceStream(AreaSeedResource)
            ?? throw new InvalidOperationException($"{AreaSeedResource} is not embedded in the Application assembly.");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("areas").EnumerateArray()
            .Select(a => (
                a.GetProperty("question").GetString()!,
                a.GetProperty("pattern").GetString()!,
                a.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).ToArray()))
            .ToList();
    }

    /// <summary>The area as a matcher, or null when its pattern or options do not read (the area is then skipped).</summary>
    private static CommandArea? Compile(string question, string pattern, string[] options)
    {
        try
        {
            return new CommandArea(question, new Regex(pattern, AreaOptions), options);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed class Snapshot
    {
        public required HashSet<string> StopWords { get; init; }
        public required HashSet<string> GenericWords { get; init; }
        public required Dictionary<string, IReadOnlyCollection<string>> IntentWords { get; init; }
        public required IReadOnlyList<CommandArea> Areas { get; init; }

        public static Snapshot FromSeed() => new()
        {
            StopWords = SeedWords("stopWords").ToHashSet(StringComparer.OrdinalIgnoreCase),
            GenericWords = SeedWords("genericWords").ToHashSet(StringComparer.OrdinalIgnoreCase),
            IntentWords = IntentCatalog.All.ToDictionary(
                i => i.Id,
                i => (IReadOnlyCollection<string>)(i.Keywords ?? Array.Empty<string>()).Select(k => k.ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal),
            Areas = SeedAreas().Select(a => Compile(a.Question, a.Pattern, a.Options)).OfType<CommandArea>().ToList(),
        };

        public static Snapshot FromRows(IEnumerable<KeywordRule> rows, IEnumerable<FeatureAreaRow> areaRows)
        {
            var list = rows.ToList();
            var stop = list.Where(r => r.IntentId is null && r.Active).Select(r => r.Word.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var generic = list.Where(r => r.IntentId == GenericGroup && r.Active).Select(r => r.Word.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var intents = list.Where(r => r.IntentId is not null && r.IntentId != GenericGroup).GroupBy(r => r.IntentId!, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyCollection<string>)g.Where(r => r.Active).Select(r => r.Word.ToLowerInvariant())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.Ordinal);
            var areas = areaRows.Where(a => a.Active).OrderBy(a => a.Id)
                .Select(a => Compile(a.Question, a.Pattern, ReadOptions(a.Options)))
                .OfType<CommandArea>().ToList();
            return new Snapshot { StopWords = stop, GenericWords = generic, IntentWords = intents, Areas = areas };
        }

        private static string[] ReadOptions(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
