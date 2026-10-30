using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Admin;

/// <summary>One label and how many messages carried it.</summary>
public sealed record UnplacedCountDto(string Key, int Count);

/// <summary>What the bot could not place in the window: by stage, by router intent, and the most common masked texts.</summary>
public sealed record UnplacedReportDto(
    int Days, int Total, IReadOnlyList<UnplacedCountDto> Stages, IReadOnlyList<UnplacedCountDto> RouterIntents,
    IReadOnlyList<UnplacedCountDto> TopMaskedTexts, string Note);

public static class UnplacedReport
{
    private const int TopTexts = 50;

    /// <summary>Captured messages are kept this long, then deleted by the scheduler.</summary>
    public const int RetentionDays = 30;

    public static async Task<UnplacedReportDto> BuildAsync(AppDbContext db, int days, DateTime nowUtc, CancellationToken ct)
    {
        var from = nowUtc.AddDays(-days);
        var rows = await db.UnplacedUtterances.AsNoTracking()
            .Where(u => u.CreatedAt >= from)
            .Select(u => new { u.Stage, u.RouterIntent, u.MaskedText })
            .ToListAsync(ct);

        return new UnplacedReportDto(
            days,
            rows.Count,
            Top(rows.Select(r => r.Stage), int.MaxValue),
            Top(rows.Select(r => r.RouterIntent ?? "(none)"), int.MaxValue),
            Top(rows.Select(r => r.MaskedText), TopTexts),
            "Masked text only: names, phones and amounts are removed before storage. Rows older than the retention window are purged.");
    }

    /// <summary>Deletes captured rows older than the retention window. Returns how many were removed.</summary>
    public static Task<int> PurgeOlderThanAsync(IAppDbContext db, DateTime nowUtc, CancellationToken ct) =>
        db.UnplacedUtterances.Where(u => u.CreatedAt < nowUtc.AddDays(-RetentionDays)).ExecuteDeleteAsync(ct);

    private static IReadOnlyList<UnplacedCountDto> Top(IEnumerable<string> keys, int take) =>
        keys.GroupBy(k => k).Select(g => new UnplacedCountDto(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Key, StringComparer.Ordinal).Take(take).ToList();
}
