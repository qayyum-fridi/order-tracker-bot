using System.Collections.Concurrent;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Counts the number words a voice note used that <see cref="SpokenNumbers"/> could not read (the word only, never the transcript or the seller),
/// so the spelling list can grow from real use. In memory: it starts again at every restart.
/// </summary>
public static class UnknownNumberWordLog
{
    private const int MaxDistinctWords = 500;
    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase);

    public static void Record(IEnumerable<string> words)
    {
        foreach (var raw in words)
        {
            var word = raw.Trim().ToLowerInvariant();
            if (word.Length is < 3 or > 30 || !word.All(char.IsLetter)) continue;
            if (!Counts.ContainsKey(word) && Counts.Count >= MaxDistinctWords) continue;
            Counts.AddOrUpdate(word, 1, (_, n) => n + 1);
        }
    }

    /// <summary>Most frequent first.</summary>
    public static IReadOnlyList<(string Word, int Count)> Snapshot() =>
        Counts.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();

    internal static void Clear() => Counts.Clear();
}
