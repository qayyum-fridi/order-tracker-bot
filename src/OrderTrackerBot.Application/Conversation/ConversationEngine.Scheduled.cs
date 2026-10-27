using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

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

                if (BillingApplies(seller) && seller.SubscriptionActiveUntil is null && seller.TrialReminderSentAt is null
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

    /// <summary>WhatsApp accepts free-form text only within 24 hours of the seller's last message.</summary>
    private async Task<bool> WithinServiceWindowAsync(Seller seller, CancellationToken ct)
    {
        var lastInbound = await _db.MessageLogs.AsNoTracking()
            .Where(m => m.Phone == seller.WhatsAppPhoneNumber && m.Direction == "inbound")
            .OrderByDescending(m => m.Id).Select(m => (DateTime?)m.CreatedAt).FirstOrDefaultAsync(ct);
        return lastInbound is { } at && DateTime.UtcNow - at < TimeSpan.FromHours(24);
    }

    /// <summary>Sends a proactive text now inside the window; outside it the text waits for the seller's next message (see <see cref="SendPendingNoticesAsync"/>).</summary>
    private async Task ProactiveReplyAsync(Seller seller, string text, CancellationToken ct)
    {
        if (await WithinServiceWindowAsync(seller, ct)) await ReplyAsync(seller, text, ct);
        else QueueProactiveNotice(seller, text);
    }

    /// <summary>
    /// Keeps the text in the seller's session. Meta would refuse it after the window, so the reminder used to be lost for good.
    /// The session is saved with the scheduler's own SaveChanges. Only the last few are kept, so a long gap cannot grow the session without limit.
    /// </summary>
    private void QueueProactiveNotice(Seller seller, string text)
    {
        if (seller.Session is null)
        {
            _logger?.LogWarning("Proactive message for seller {SellerId} not kept: no session", seller.Id);
            return;
        }
        var ctx = SessionContextData.FromJson(seller.Session.ContextJson);
        ctx.PendingNotices = (ctx.PendingNotices ?? new List<string>()).Append(text).TakeLast(MaxPendingNotices).ToList();
        seller.Session.ContextJson = ctx.ToJson();
        _logger?.LogInformation("Proactive message for seller {SellerId} kept until the next message (outside the 24-hour window)", seller.Id);
    }

    private const int MaxPendingNotices = 5;

    /// <summary>The seller has just written, so the window is open: the proactive messages kept for them go out first, with a heading.</summary>
    private async Task SendPendingNoticesAsync(Seller seller, CancellationToken ct)
    {
        if (seller.Session is null) return;
        var ctx = SessionContextData.FromJson(seller.Session.ContextJson);
        if (ctx.PendingNotices is not { Count: > 0 } notices) return;
        ctx.PendingNotices = null;
        seller.Session.ContextJson = ctx.ToJson();
        foreach (var notice in notices)
            await ReplyAsync(seller, MessageTemplates.Get("scheduled.003", notice), ct);
    }

    private async Task SendWeeklySummaryAsync(Seller seller, DateTime weekEndUtc, CancellationToken ct)
    {
        var since = weekEndUtc.AddDays(-7);
        var orders = await _db.Orders.Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= since && o.CreatedAt < weekEndUtc)
            .ToListAsync(ct);

        if (orders.Count == 0)
        {
            await ProactiveReplyAsync(seller, MessageTemplates.Get("scheduled.001", seller.BusinessName), ct);
            return;
        }

        var active = orders.Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned).ToList();
        var top = active.SelectMany(o => o.Items).GroupBy(i => i.ProductNameSnapshot)
            .Select(g => new { Name = g.Key, Orders = g.Select(i => i.OrderId).Distinct().Count() })
            .OrderByDescending(g => g.Orders).FirstOrDefault();

        var text = MessageTemplates.Get("scheduled.002", seller.BusinessName, orders.Count, orders.Count(o => o.Status == OrderStatus.Delivered), orders.Count(o => o.Status == OrderStatus.Pending), orders.Count(o => o.Status == OrderStatus.Cancelled), orders.Count(o => o.Status == OrderStatus.Returned), (top is null ? "" : $"Top product: {top.Name} ({top.Orders} orders)\n"), Formatters.Money(active.Sum(o => o.Total)), (active.Sum(o => o.TaxWithheld) is > 0 and var withheld ? $"Tax withheld by courier/gateway: {Formatters.Money(withheld)}\n" : ""));

        if (await WithinServiceWindowAsync(seller, ct))
        {
            await ReplyAsync(seller, text, ct);
            return;
        }

        // Outside the 24-hour window only the approved weekly_summary template reaches the seller. Its body already says "Rs.", so the total is sent as a bare number.
        var total = active.Sum(o => o.Total).ToString("#,0.##", CultureInfo.InvariantCulture);
        var sent = await _sender.SendTemplateMessageAsync(seller.WhatsAppPhoneNumber,
            new[] { active.Count.ToString(CultureInfo.InvariantCulture), total }, WhatsAppTemplateKeys.Weekly, ct);
        if (!sent) QueueProactiveNotice(seller, text);
    }
}
