using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Proactive messages: the weekly summary (screen 9, Sunday 9:00 PKT) and the trial-ending reminder (screen 1d).
public partial class ConversationEngine
{
    /// <summary>Pakistan Standard Time, UTC+5 with no DST.</summary>
    private static readonly TimeSpan PakistanOffset = TimeSpan.FromHours(5);

    /// <summary>Called periodically by a background service; each message goes out at most once per seller.</summary>
    public async Task RunScheduledJobsAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var local = utcNow + PakistanOffset;
        var lastSundayNineLocal = local.Date.AddDays(-(int)local.DayOfWeek).AddHours(9);
        if (lastSundayNineLocal > local) lastSundayNineLocal = lastSundayNineLocal.AddDays(-7);
        var weeklyDueUtc = lastSundayNineLocal - PakistanOffset;

        var sellers = await _db.Sellers.Where(s => s.OnboardingComplete).ToListAsync(ct);
        foreach (var seller in sellers)
        {
            if ((seller.LastWeeklySummaryAt is null || seller.LastWeeklySummaryAt < weeklyDueUtc) && seller.CreatedAt < weeklyDueUtc)
            {
                await SendWeeklySummaryAsync(seller, weeklyDueUtc, ct);
                seller.LastWeeklySummaryAt = utcNow;
            }

            if (_billing.Enabled && seller.SubscriptionActiveUntil is null && seller.TrialReminderSentAt is null
                && seller.TrialEndsAt is { } end && end > utcNow && end - utcNow <= TimeSpan.FromDays(2))
                await SendTrialEndingReminderAsync(seller, ct);

            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task SendWeeklySummaryAsync(Seller seller, DateTime weekEndUtc, CancellationToken ct)
    {
        var since = weekEndUtc.AddDays(-7);
        var orders = await _db.Orders.Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= since && o.CreatedAt < weekEndUtc)
            .ToListAsync(ct);

        if (orders.Count == 0)
        {
            await ReplyAsync(seller, $"📊 Weekly Summary — {seller.BusinessName}\n\nIs hafte koi order record nahi hua. Naya order aate hi forward kar dein! 💪", ct);
            return;
        }

        var active = orders.Where(o => o.Status != OrderStatus.Cancelled).ToList();
        var top = active.SelectMany(o => o.Items).GroupBy(i => i.ProductNameSnapshot)
            .Select(g => new { Name = g.Key, Orders = g.Select(i => i.OrderId).Distinct().Count() })
            .OrderByDescending(g => g.Orders).FirstOrDefault();

        await ReplyAsync(seller,
            $"📊 Weekly Summary — {seller.BusinessName}\n\n" +
            $"Total orders: {orders.Count}\n" +
            $"Delivered: {orders.Count(o => o.Status == OrderStatus.Delivered)} | Pending: {orders.Count(o => o.Status == OrderStatus.Pending)} | " +
            $"Cancelled: {orders.Count(o => o.Status == OrderStatus.Cancelled)}\n" +
            (top is null ? "" : $"Top product: {top.Name} ({top.Orders} orders)\n") +
            $"Total sales: {Formatters.Money(active.Sum(o => o.Total))}\n\nKeep it up! 🎉", ct);
    }
}
