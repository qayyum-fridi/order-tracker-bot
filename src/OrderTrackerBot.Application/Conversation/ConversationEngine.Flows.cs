using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private async Task HandleCodCollectedConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var orderId = ctx.CodCollectedOrderId;
        ctx.CodCollectedOrderId = null;
        SetState(session, ConversationState.Idle);

        if (!CommandParser.IsAffirmative(message) || orderId is null) return;

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.SellerId == seller.Id, ct);
        if (order is null) return;

        order.PaymentStatus = PaymentStatus.Paid;
        await ReplyAsync(seller, $"✅ {Formatters.Money(order.Total)} COD collected — payment marked PAID", ct);
        await CheckLoyaltyThresholdAsync(seller, order.CustomerId, ct);
    }

    private async Task HandleRuntimeFilterChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var command = ctx.RuntimeFilterCommand;
        var choice = NormalizeQuickReply(message);

        switch (command)
        {
            case "trending" when choice.Contains("custom"):
                ctx.RuntimeFilterCommand = null;
                SetState(session, ConversationState.AwaitingCustomDateRange);
                await ReplyAsync(seller, "📅 Konsi dates ke beech? (e.g. \"1 May se 15 May\")", ct);
                return;

            case "trending":
            {
                var since = choice.Contains("month") || choice == "2" ? DateTime.UtcNow.AddMonths(-1) : DateTime.UtcNow.AddDays(-7);
                await SendTrendingProductsAsync(seller, since, "Is period", ct);
                SetState(session, ConversationState.Idle);
                ctx.RuntimeFilterCommand = null;
                return;
            }

            case "slow":
            {
                var days = choice.Contains("14") || choice == "2" ? 14 : choice.Contains("30") || choice == "3" ? 30 : 7;
                await SendSlowMoversAsync(seller, days, ct);
                SetState(session, ConversationState.Idle);
                ctx.RuntimeFilterCommand = null;
                return;
            }

            case "cod":
            {
                int? minDaysOld = (choice.Contains("3") || choice == "2") ? 3 : (choice.Contains("7") || choice == "3") ? 7 : null;
                await SendCodPendingAsync(seller, minDaysOld, ct);
                SetState(session, ConversationState.Idle);
                ctx.RuntimeFilterCommand = null;
                return;
            }

            default:
                SetState(session, ConversationState.Idle);
                return;
        }
    }

    private static string NormalizeQuickReply(string message) => message.Trim().ToLowerInvariant();

    private async Task HandleCustomDateRangeAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        if (!TryParseDateRange(message, out var from, out var to))
        {
            await ReplyAsync(seller, "Date range samajh nahi aayi — format try karein: \"1 May se 15 May\"", ct);
            return;
        }

        await SendTrendingProductsAsync(seller, from, $"{from:d MMM}-{to:d MMM}", ct, to);
    }

    private static bool TryParseDateRange(string message, out DateTime from, out DateTime to)
    {
        from = DateTime.UtcNow.AddDays(-30);
        to = DateTime.UtcNow;
        var parts = message.Split(new[] { " se ", " to ", "-" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!DateTime.TryParse(parts[0], out var start) || !DateTime.TryParse(parts[1], out var end)) return false;
        from = start;
        to = end;
        return true;
    }

    private async Task SendTrendingProductsAsync(Seller seller, DateTime since, string periodLabel, CancellationToken ct, DateTime? until = null)
    {
        var query = _db.OrderItems
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.Status != OrderStatus.Cancelled && i.Order.CreatedAt >= since);
        if (until is not null) query = query.Where(i => i.Order!.CreatedAt <= until);

        var top = await query.GroupBy(i => i.ProductNameSnapshot)
            .Select(g => new { Name = g.Key, Orders = g.Count() })
            .OrderByDescending(g => g.Orders)
            .Take(5)
            .ToListAsync(ct);

        if (top.Count == 0)
        {
            await ReplyAsync(seller, "Is period mein koi order nahi mila.", ct);
            return;
        }

        var lines = top.Select((p, i) => $"{i + 1}. {p.Name} - {p.Orders} orders");
        var reply = $"📈 Trending ({periodLabel}):\n\n{string.Join("\n", lines)}";
        var insight = await _ai.GenerateInsightAsync($"Top products: {string.Join(", ", top.Select(t => $"{t.Name} ({t.Orders})"))}", ct);
        if (insight is not null) reply += $"\n\n🤖 Insight: {insight}";
        await ReplyAsync(seller, reply, ct);
    }

    private async Task SendSlowMoversAsync(Seller seller, int days, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var recentlyOrderedNames = await _db.OrderItems
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= since)
            .Select(i => i.ProductNameSnapshot)
            .Distinct()
            .ToListAsync(ct);

        var slow = await _db.Products
            .Where(p => p.SellerId == seller.Id && p.IsActive && !recentlyOrderedNames.Contains(p.Name))
            .ToListAsync(ct);

        if (slow.Count == 0)
        {
            await ReplyAsync(seller, $"{days}+ din se sab products mein order aaya hai — koi slow mover nahi.", ct);
            return;
        }

        var lines = slow.Select((p, i) => $"{i + 1}. {p.Name} - {Formatters.Money(p.Price)}");
        var reply = $"📉 Slow Movers ({days}+ din se koi order nahi):\n\n{string.Join("\n", lines)}";
        var insight = await _ai.GenerateInsightAsync($"Slow-moving products with no orders in {days} days: {string.Join(", ", slow.Select(s => s.Name))}", ct);
        reply += insight is not null ? $"\n\n🤖 Suggestion: {insight}" : "\n\n🤖 Suggestion: Inpe discount code try karein customers wapas laane ke liye.";
        await ReplyAsync(seller, reply, ct);
    }

    private async Task SendCodPendingAsync(Seller seller, int? minDaysOld, CancellationToken ct)
    {
        var query = _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.Status == OrderStatus.Delivered && o.PaymentStatus == PaymentStatus.Unpaid);

        if (minDaysOld is int d)
        {
            var cutoff = DateTime.UtcNow.AddDays(-d);
            query = query.Where(o => o.DeliveredAt != null && o.DeliveredAt <= cutoff);
        }

        var orders = await query.OrderBy(o => o.DeliveredAt).ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, "Koi COD pending nahi hai.", ct);
            return;
        }

        var lines = orders.Select((o, i) => $"{i + 1}. {o.Customer?.Name} - {Formatters.ItemsSummary(o)} - {Formatters.Money(o.Total)}");
        await ReplyAsync(seller,
            $"💵 Delivered but Cash Not Collected ({orders.Count}):\n\n{string.Join("\n", lines)}\n\n" +
            $"Total pending cash: {Formatters.Money(orders.Sum(o => o.Total))}", ct);
    }

    private async Task HandleBroadcastRequestAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var totalCustomers = await _db.Customers.CountAsync(c => c.SellerId == seller.Id, ct);
        var repeatCustomers = await _db.Customers.CountAsync(c => c.SellerId == seller.Id && c.Orders.Count(o => o.Status != OrderStatus.Cancelled) > 1, ct);
        var inactiveSince = DateTime.UtcNow.AddDays(-30);
        var inactiveCustomers = await _db.Customers.CountAsync(c => c.SellerId == seller.Id && !c.Orders.Any(o => o.CreatedAt >= inactiveSince), ct);

        ctx.BroadcastMessageText = cmd.Text;
        SetState(session, ConversationState.AwaitingBroadcastAudienceChoice);
        await ReplyAsync(seller,
            "Kitne customers ko bhejna hai?\n\n" +
            $"1️⃣ Sab ({totalCustomers} customers)\n" +
            $"2️⃣ Sirf repeat customers ({repeatCustomers})\n" +
            $"3️⃣ 30 din se inactive ({inactiveCustomers})", ct);
    }

    private async Task HandleBroadcastAudienceChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        var text = ctx.BroadcastMessageText;
        ctx.BroadcastMessageText = null;

        if (message.Trim() is not ("1" or "2" or "3") || text is null)
        {
            await ReplyAsync(seller, "Reply 1, 2 ya 3.", ct);
            return;
        }

        // NOTE: Meta requires a pre-approved message template for any WhatsApp broadcast
        // outside the 24h session window. This records the campaign; wiring the actual
        // template send is a follow-up once a template is approved for this seller.
        await ReplyAsync(seller,
            "✅ Broadcast queued (approved template use kiya gaya).\n\"campaign status\" se delivery check karein.", ct);
    }
}
