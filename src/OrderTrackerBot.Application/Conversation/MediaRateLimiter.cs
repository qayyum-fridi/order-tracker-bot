using System.Collections.Concurrent;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>Section "MediaLimits": per-seller caps on paid AI media per rolling hour. 0 means unlimited.</summary>
public sealed class MediaLimitOptions
{
    public const string SectionName = "MediaLimits";

    public int VoicePerHour { get; set; } = 10;
    public int ImagePerHour { get; set; } = 10;
}

public enum MediaKind { Voice, Image }

/// <summary>
/// Rolling one-hour window per seller and kind, kept in memory (a restart resets it — this guards cost, not security).
/// Only checked before the paid transcription / vision call, so a refused message costs nothing.
/// </summary>
public sealed class MediaRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly MediaLimitOptions _options;
    private readonly ConcurrentDictionary<(string Phone, MediaKind Kind), Queue<DateTime>> _hits = new();

    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public MediaRateLimiter(MediaLimitOptions options) => _options = options;

    public int LimitFor(MediaKind kind) => kind == MediaKind.Voice ? _options.VoicePerHour : _options.ImagePerHour;

    /// <summary>Records one use and returns true, or returns false (and how long until a slot frees) when the hour is full.</summary>
    public bool TryConsume(string phone, MediaKind kind, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var limit = LimitFor(kind);
        if (limit <= 0) return true;

        var now = UtcNow();
        var queue = _hits.GetOrAdd((phone, kind), _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window) queue.Dequeue();
            if (queue.Count >= limit)
            {
                retryAfter = queue.Peek() + Window - now;
                return false;
            }
            queue.Enqueue(now);
            return true;
        }
    }
}
