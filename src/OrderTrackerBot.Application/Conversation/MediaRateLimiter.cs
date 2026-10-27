using System.Collections.Concurrent;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Section "MediaLimits": hourly caps per seller. Each kind has a count cap; the USD budget caps what one seller can cost in a rolling hour.
/// Free sellers get <see cref="FreeUsdPerHour"/>, sellers on a plan or trial get <see cref="PaidUsdPerHour"/>. 0 = unlimited.
/// Unit costs are estimates for the models in use, not billed amounts; check them against the OpenAI price page.
/// </summary>
public sealed class MediaLimitOptions
{
    public const string SectionName = "MediaLimits";

    public int TextPerHour { get; set; } = 150;
    public int VoicePerHour { get; set; } = 30;
    public int ImagePerHour { get; set; } = 15;

    public decimal TextUsd { get; set; } = 0.002m;
    public decimal VoiceUsd { get; set; } = 0.015m;
    public decimal ImageUsd { get; set; } = 0.01m;

    public decimal FreeUsdPerHour { get; set; } = 1.00m;
    public decimal PaidUsdPerHour { get; set; } = 5.00m;
}

public enum MediaKind { Text, Voice, Image }

/// <summary>Why a use was refused: the count for its kind is full, or the hour's USD budget would be exceeded.</summary>
public enum UsageDecision { Allowed, CountLimit, BudgetLimit }

/// <summary>
/// Rolling one-hour window per seller, kept in memory (a restart resets it — this guards cost, not security).
/// Checked before the paid call, so a refused message costs nothing.
/// </summary>
public sealed class MediaRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly MediaLimitOptions _options;
    private readonly ConcurrentDictionary<string, Queue<Hit>> _hits = new();

    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public MediaRateLimiter(MediaLimitOptions options) => _options = options;

    public MediaLimitOptions Options => _options;

    public int LimitFor(MediaKind kind) => kind switch
    {
        MediaKind.Text => _options.TextPerHour,
        MediaKind.Voice => _options.VoicePerHour,
        _ => _options.ImagePerHour
    };

    public decimal CostFor(MediaKind kind) => kind switch
    {
        MediaKind.Text => _options.TextUsd,
        MediaKind.Voice => _options.VoiceUsd,
        _ => _options.ImageUsd
    };

    /// <summary>Records one use when both the count cap and the USD budget (0 = no budget) allow it.</summary>
    public UsageDecision Consume(string phone, MediaKind kind, decimal budgetUsd, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var now = UtcNow();
        var queue = _hits.GetOrAdd(phone, _ => new Queue<Hit>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek().At >= Window) queue.Dequeue();

            var limit = LimitFor(kind);
            if (limit > 0 && queue.Count(h => h.Kind == kind) >= limit)
            {
                retryAfter = queue.First(h => h.Kind == kind).At + Window - now;
                return UsageDecision.CountLimit;
            }

            var cost = CostFor(kind);
            if (budgetUsd > 0 && queue.Sum(h => h.Usd) + cost > budgetUsd)
            {
                // Approximate: the oldest hit leaving the window is when the budget starts to free up.
                retryAfter = queue.Peek().At + Window - now;
                return UsageDecision.BudgetLimit;
            }

            queue.Enqueue(new Hit(now, kind, cost));
            return UsageDecision.Allowed;
        }
    }

    /// <summary>Count cap only (no USD budget). Returns false and how long until a slot frees when the hour is full.</summary>
    public bool TryConsume(string phone, MediaKind kind, out TimeSpan retryAfter) =>
        Consume(phone, kind, budgetUsd: 0, out retryAfter) == UsageDecision.Allowed;

    private readonly record struct Hit(DateTime At, MediaKind Kind, decimal Usd);
}
