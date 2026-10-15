using System.Collections.Concurrent;

namespace OrderTrackerBot.Api.Client;

/// <summary>
/// Limits how often a dashboard login can be tried for one phone number, whether or not the number exists, so nobody can
/// flood a seller's login or probe numbers quickly. In memory: a restart resets it, which is fine for a window of minutes.
/// </summary>
public sealed class ClientLoginLimiter
{
    public const int MaxChecksPerWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int PruneAbove = 5_000;

    private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _windows = new();

    /// <summary>Clock for the window. Tests replace it; production uses UTC now.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>True when this check is allowed. Every check counts, successful or not.</summary>
    public bool TryAcquire(string key)
    {
        var now = Clock();
        if (_windows.Count > PruneAbove) Prune(now);
        var entry = _windows.AddOrUpdate(
            key,
            _ => (1, now),
            (_, current) => now - current.WindowStart >= Window ? (1, now) : (current.Count + 1, current.WindowStart));
        return entry.Count <= MaxChecksPerWindow;
    }

    private void Prune(DateTime now)
    {
        foreach (var (key, value) in _windows)
            if (now - value.WindowStart >= Window) _windows.TryRemove(key, out _);
    }
}
