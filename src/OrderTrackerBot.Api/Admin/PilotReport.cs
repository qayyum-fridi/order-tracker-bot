using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Admin;

/// <summary>One seller's pilot numbers for the window. "Fixes" are proxies for a wrong order: an undo, edit or cancel logged soon after the order was saved.</summary>
public sealed record PilotSellerDto(
    int SellerId, string? BusinessName, string Phone, string Plan, DateTime? LastMessageAt,
    int Messages, int VoiceNotes, int NotUnderstood, int OrdersSaved, int Undone, int EditedOrCancelledWithinAnHour,
    bool ActiveThisWindow, bool ActivePreviousWindow, bool Returned, bool Paid);

public sealed record PilotTotalsDto(
    int Sellers, int ActiveSellers, int Returned, int Paid, int Messages, int VoiceNotes, int NotUnderstood,
    int OrdersSaved, int Undone, int EditedOrCancelledWithinAnHour);

public sealed record PilotReportDto(int Days, DateTime FromUtc, DateTime ToUtc, PilotTotalsDto Totals, IReadOnlyList<PilotSellerDto> Sellers, string Note);

public static class PilotReport
{
    // Stable fragments of the bot's own replies (CLAUDE.md: tests pin them), used because replies are logged as text.
    private const string NotUnderstoodFragment = "samajh nahi aaya";
    private const string VoiceEchoFragment = "Maine suna";
    private static readonly TimeSpan FixWindow = TimeSpan.FromHours(1);

    public static async Task<PilotReportDto> BuildAsync(AppDbContext db, int days, DateTime nowUtc, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 90);
        var from = nowUtc.AddDays(-days);
        var previousFrom = from.AddDays(-days);

        var sellers = await db.Sellers.AsNoTracking()
            .Select(s => new { s.Id, s.BusinessName, s.WhatsAppPhoneNumber, s.Plan, s.SubscriptionActiveUntil })
            .ToListAsync(ct);
        var logs = await db.MessageLogs.AsNoTracking().Where(m => m.CreatedAt >= previousFrom)
            .Select(m => new { m.Phone, m.Direction, m.RawText, m.CreatedAt }).ToListAsync(ct);
        var orders = await db.Orders.AsNoTracking().Where(o => o.CreatedAt >= from)
            .Select(o => new { o.Id, o.SellerId, o.CreatedAt }).ToListAsync(ct);
        var actions = await db.ActionLogs.AsNoTracking().Where(a => a.CreatedAt >= from)
            .Select(a => new { a.SellerId, a.ActionType, a.OrderId, a.CreatedAt, a.Undone }).ToListAsync(ct);

        var rows = new List<PilotSellerDto>();
        foreach (var seller in sellers)
        {
            var mine = logs.Where(l => l.Phone == seller.WhatsAppPhoneNumber).ToList();
            var inbound = mine.Where(l => l.Direction == "inbound").ToList();
            var thisWindow = inbound.Where(l => l.CreatedAt >= from).ToList();
            var outboundNow = mine.Where(l => l.Direction == "outbound" && l.CreatedAt >= from).ToList();
            var savedOrders = orders.Where(o => o.SellerId == seller.Id).ToList();
            var fixes = actions.Where(a => a.SellerId == seller.Id && a.OrderId is not null
                && (a.ActionType is ActionType.OrderEdited or ActionType.OrderCancelled)
                && savedOrders.Any(o => o.Id == a.OrderId && a.CreatedAt - o.CreatedAt <= FixWindow)).Count();
            var activeNow = thisWindow.Count > 0;
            var activeBefore = inbound.Any(l => l.CreatedAt < from);
            rows.Add(new PilotSellerDto(
                seller.Id, seller.BusinessName, seller.WhatsAppPhoneNumber, seller.Plan.ToString(),
                inbound.Count == 0 ? null : inbound.Max(l => l.CreatedAt),
                thisWindow.Count,
                outboundNow.Count(l => l.RawText.Contains(VoiceEchoFragment, StringComparison.OrdinalIgnoreCase)),
                outboundNow.Count(l => l.RawText.Contains(NotUnderstoodFragment, StringComparison.OrdinalIgnoreCase)),
                savedOrders.Count,
                actions.Count(a => a.SellerId == seller.Id && a.Undone),
                fixes,
                activeNow, activeBefore, activeNow && activeBefore,
                seller.Plan is SubscriptionPlan.Basic or SubscriptionPlan.Pro && seller.SubscriptionActiveUntil > nowUtc));
        }

        rows = rows.OrderByDescending(r => r.OrdersSaved).ThenByDescending(r => r.Messages).ToList();
        var totals = new PilotTotalsDto(
            rows.Count, rows.Count(r => r.ActiveThisWindow), rows.Count(r => r.Returned), rows.Count(r => r.Paid),
            rows.Sum(r => r.Messages), rows.Sum(r => r.VoiceNotes), rows.Sum(r => r.NotUnderstood),
            rows.Sum(r => r.OrdersSaved), rows.Sum(r => r.Undone), rows.Sum(r => r.EditedOrCancelledWithinAnHour));
        return new PilotReportDto(days, from, nowUtc, totals, rows,
            "Counts only; accuracy of voice/AI needs a human to review transcripts. Fixes = order edited or cancelled within 1 hour of saving. " +
            "Returned = messaged in both this and the previous window. Paid = Basic/Pro with an unexpired subscription (the seller's own 'paid' claim is not verified).");
    }
}
