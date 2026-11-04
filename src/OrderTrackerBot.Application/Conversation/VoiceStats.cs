using System.Collections.Concurrent;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Counts what happened to voice notes, by outcome name only (never the transcript, the audio or the seller), so a pilot shows how often a note ran first time,
/// needed a second try or was refused. In memory: it starts again at every restart.
/// </summary>
public static class VoiceStats
{
    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);

    public static void Record(string outcome) => Counts.AddOrUpdate(outcome, 1, (_, n) => n + 1);

    /// <summary>Most frequent first.</summary>
    public static IReadOnlyList<(string Outcome, int Count)> Snapshot() =>
        Counts.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();

    internal static void Clear() => Counts.Clear();
}
