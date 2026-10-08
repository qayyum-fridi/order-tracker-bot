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

        // Meta stops redelivering well within a day; a week of message-id claims keeps the dedupe table small.
        try
        {
            var cutoff = utcNow.AddDays(-7);
            await _db.ProcessedWebhookMessages.Where(m => m.ProcessedAt < cutoff).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await ReportScheduledIssueAsync(null, "purge processed webhook ids", ex, ct);
        }

        var sellers = await _db.Sellers.Where(s => s.OnboardingComplete).ToListAsync(ct);
        foreach (var seller in sellers)
        {
            try
            {
                if ((seller.LastWeeklySummaryAt is null || seller.LastWeeklySummaryAt < weeklyDueUtc) && seller.CreatedAt < weeklyDueUtc)
                {
                    // Atomic claim: only the run whose UPDATE still sees the stale timestamp sends — never twice, even with two instances.
                    var id = seller.Id;
                    var claimed = await _db.Sellers
                        .Where(s => s.Id == id && (s.LastWeeklySummaryAt == null || s.LastWeeklySummaryAt < weeklyDueUtc))
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastWeeklySummaryAt, utcNow), ct);
                    if (claimed == 1)
                    {
                        seller.LastWeeklySummaryAt = utcNow;
                        await SendWeeklySummaryAsync(seller, weeklyDueUtc, ct);
                    }
                }

                if (_billing.Enabled && seller.SubscriptionActiveUntil is null && seller.TrialReminderSentAt is null
                    && seller.TrialEndsAt is { } end && end > utcNow && end - utcNow <= TimeSpan.FromDays(2))
                {
                    var id = seller.Id;
                    var claimed = await _db.Sellers
                        .Where(s => s.Id == id && s.TrialReminderSentAt == null)
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.TrialReminderSentAt, utcNow), ct);
                    if (claimed == 1) await SendTrialEndingReminderAsync(seller, ct);
                }

                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One seller's failure must not stop everyone else's weekly summary.
                await ReportScheduledIssueAsync(seller.WhatsAppPhoneNumber, $"seller {seller.Id}", ex, ct);
            }
        }
    }

    private Task ReportScheduledIssueAsync(string? phone, string detail, Exception ex, CancellationToken ct) =>
        _issues?.ReportAsync(Abstractions.IssueCodes.ScheduledJobFailed, phone, detail, ex, ct) ?? Task.CompletedTask;

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

        var active = orders.Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned).ToList();
        var top = active.SelectMany(o => o.Items).GroupBy(i => i.ProductNameSnapshot)
            .Select(g => new { Name = g.Key, Orders = g.Select(i => i.OrderId).Distinct().Count() })
            .OrderByDescending(g => g.Orders).FirstOrDefault();

        await ReplyAsync(seller,
            $"📊 Weekly Summary — {seller.BusinessName}\n\n" +
            $"Total orders: {orders.Count}\n" +
            $"Delivered: {orders.Count(o => o.Status == OrderStatus.Delivered)} | Pending: {orders.Count(o => o.Status == OrderStatus.Pending)} | " +
            $"Cancelled: {orders.Count(o => o.Status == OrderStatus.Cancelled)} | Returned: {orders.Count(o => o.Status == OrderStatus.Returned)}\n" +
            (top is null ? "" : $"Top product: {top.Name} ({top.Orders} orders)\n") +
            $"Total sales: {Formatters.Money(active.Sum(o => o.Total))}\n" +
            (active.Sum(o => o.TaxWithheld) is > 0 and var withheld ? $"Tax withheld by courier/gateway: {Formatters.Money(withheld)}\n" : "") +
            "\nKeep it up! 🎉", ct);
    }
}
