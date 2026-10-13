using OrderTrackerBot.Application.Time;
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

        if (!CommandParser.IsAffirmative(message) || orderId is null)
        {
            // Not a yes: a real command sent instead of the answer must run, not vanish without a reply.
            if (!CommandParser.IsNegative(message) && CommandParser.TryParse(message) is { } other)
                await ExecuteCommandAsync(seller, session, ctx, other, ct);
            return;
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.SellerId == seller.Id, ct);
        if (order is null) return;

        var collected = MarkFullyPaid(seller, order);
        await ReplyAsync(seller, $"✅ {Formatters.Money(collected)} COD collected — payment marked PAID" + await DrawerHintAfterCodAsync(seller, ct), ct);
    }

    private async Task HandleRuntimeFilterChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var command = ctx.RuntimeFilterCommand;
        var choice = NormalizeQuickReply(message);

        // A different command instead of a choice: leave the filter prompt and run it.
        if (CommandParser.TryParse(message) is { } other && !int.TryParse(choice, out _))
        {
            ctx.RuntimeFilterCommand = null;
            SetState(session, ConversationState.Idle);
            await ExecuteCommandAsync(seller, session, ctx, other, ct);
            return;
        }

        switch (command)
        {
            case "trending" when choice.Contains("custom") || choice == "3":
                ctx.RuntimeFilterCommand = null;
                SetState(session, ConversationState.AwaitingCustomDateRange);
                await ReplyAsync(seller, "📅 Konsi dates ke beech? (e.g. \"1 May se 15 May\")", ct);
                return;

            case "trending":
            {
                SetState(session, ConversationState.Idle);
                ctx.RuntimeFilterCommand = null;
                var now = DateTime.UtcNow;
                // The three standard buttons keep their week-over-week comparison; any other period phrase gets its own window.
                if (ReportPeriods.TryParseKey(message, now, out var trendKey) && trendKey is not ("7d" or "30d" or "thismonth" or "thisweek")
                    && ReportPeriods.Resolve(trendKey, seller.TimeZoneId, now) is { } trendWindow)
                    await SendTrendingProductsAsync(seller, trendWindow.StartUtc, PeriodLabel(seller, trendKey), ct, trendWindow.EndUtc);
                else if (choice.Contains("30") || choice.Contains("last"))
                    await SendTrendingProductsAsync(seller, now.AddDays(-30), "Last 30 Days", ct, compareLabel: "pichle 30 din");
                else if (choice.Contains("month") || choice == "2")
                    await SendTrendingProductsAsync(seller, now.AddMonths(-1), "This Month", ct, compareLabel: "last month");
                else
                    await SendTrendingProductsAsync(seller, now.AddDays(-7), "This Week", ct, compareLabel: "last week");
                return;
            }

            case "slow":
            {
                var days = choice.StartsWith("14") || choice == "2" ? 14 : choice.StartsWith("30") || choice == "3" ? 30 : 7;
                if (ReportPeriods.TryParseKey(message, DateTime.UtcNow, out var slowKey) && ReportPeriods.Resolve(slowKey, seller.TimeZoneId, DateTime.UtcNow) is { } slowWindow)
                    days = Math.Max(1, (int)Math.Ceiling((DateTime.UtcNow - slowWindow.StartUtc).TotalDays));
                await SendSlowMoversAsync(seller, days, ct);
                SetState(session, ConversationState.Idle);
                ctx.RuntimeFilterCommand = null;
                return;
            }

            case "cod":
            {
                int? minDaysOld = choice.StartsWith("3+") || choice == "2" ? 3 : choice.StartsWith("7") || choice == "3" ? 7 : null;
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
        // Any period phrase: "1 May se 15 May", "pichle 3 mahine", "last quarter", "5 May", "this year".
        if (ReportPeriods.TryParseKey(message, DateTime.UtcNow, out var periodKey)
            && ReportPeriods.Resolve(periodKey, seller.TimeZoneId, DateTime.UtcNow) is { } customWindow)
        {
            await SendTrendingProductsAsync(seller, customWindow.StartUtc, PeriodLabel(seller, periodKey), ct, customWindow.EndUtc);
            return;
        }

        // "1 January se aaj tak": from a date until now.
        if (TryParseOpenRange(message, out var openFrom))
        {
            await SendTrendingProductsAsync(seller, openFrom, $"{openFrom:d MMM} se aaj tak", ct, DateTime.UtcNow.AddDays(1));
            return;
        }

        if (!TryParseDateRange(message, out var from, out var to))
        {
            // Not dates at all: a real command ("summary last week", "orders today") or a way out is not an answer to the date question. Leave it and run it.
            if (CancelWords.Contains(message.Trim()))
            {
                await ReplyAsync(seller, "Theek hai, tareekh ka sawal band kar diya.", ct);
                return;
            }
            if (CommandParser.TryParse(message) is { } command)
            {
                await ExecuteCommandAsync(seller, session, ctx, command, ct);
                return;
            }
            SetState(session, ConversationState.AwaitingCustomDateRange); // still waiting: a wrong try must not lose the question
            await ReplyAsync(seller, "Maaf kijiye, tareekh samajh nahi aayi. Barae meherbani likhein: \"1 May se 15 May\", \"1 January se aaj tak\", \"pichle 3 mahine\" ya \"last quarter\". Rokne ke liye \"cancel\".", ct);
            return;
        }

        var label = from.Month == to.Month ? $"{from.Day}-{to:d MMM}" : $"{from:d MMM}-{to:d MMM}";
        await SendTrendingProductsAsync(seller, from, label, ct, to.AddDays(1));
    }

    private static readonly string[] DateFormats = { "d MMM", "d MMMM", "d MMM yyyy", "d MMMM yyyy", "d/M", "d/M/yyyy", "yyyy-MM-dd", "MMM d", "MMMM d" };

    private static readonly System.Text.RegularExpressions.Regex OpenRange = new(
        @"^(?:since\s+)?(?<from>.+?)\s+(?:se|from|to|till|until)\s+(?:ab|abhi|aab|aaj|aj|now|today|date|present)(?:\s+tak)?$|^(?:since|from)\s+(?<from>.+)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>"1 January se aaj tak" / "since 1 January" / "1 Jan 2026 se ab tak": a start date and "until now". A day said as "yakum" / "یکم" counts as 1.</summary>
    internal static bool TryParseOpenRange(string message, out DateTime from)
    {
        from = default;
        var m = OpenRange.Match(message.Trim().TrimEnd('.', '،', '۔'));
        if (!m.Success) return false;
        var text = System.Text.RegularExpressions.Regex.Replace(m.Groups["from"].Value.Trim(), @"\b(?:yakum|yekam|1st)\b|یکم", "1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var style = System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal;
        if (!DateTime.TryParseExact(text, DateFormats, culture, style, out var start)) return false;
        if (start > DateTime.UtcNow) start = start.AddYears(-1); // no year typed: the most recent such date
        from = start;
        return true;
    }

    private static bool TryParseDateRange(string message, out DateTime from, out DateTime to)
    {
        from = DateTime.UtcNow.AddDays(-30);
        to = DateTime.UtcNow;
        var parts = message.Split(new[] { " se ", " to ", " tak ", " - ", "–" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var style = System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal;
        if (!DateTime.TryParseExact(parts[0].Trim(), DateFormats, culture, style, out var start)
            || !DateTime.TryParseExact(parts[1].Replace("tak", "").Trim(), DateFormats, culture, style, out var end)) return false;
        // No year typed means the most recent such date.
        if (start > DateTime.UtcNow) start = start.AddYears(-1);
        if (end < start) end = end.AddYears(1);
        from = start;
        to = end;
        return true;
    }

    private async Task SendTrendingProductsAsync(Seller seller, DateTime since, string periodLabel, CancellationToken ct, DateTime? until = null, string? compareLabel = null)
    {
        var end = until ?? DateTime.UtcNow;
        var counts = await ProductOrderCountsAsync(seller, since, end, ct);
        var top = counts.OrderByDescending(kv => kv.Value).Take(5).ToList();

        if (top.Count == 0)
        {
            await ReplyAsync(seller, "Is period mein koi order nahi mila.", ct);
            return;
        }

        var previous = compareLabel is null ? null : await ProductOrderCountsAsync(seller, since - (end - since), since, ct);
        string Trend(string name, int now)
        {
            if (previous is null) return "";
            var before = previous.GetValueOrDefault(name);
            return now > before ? $" (↑ from {before} {compareLabel})" : now < before ? $" (↓ from {before} {compareLabel})" : " (steady)";
        }

        var lines = top.Select((p, i) => $"{i + 1}. {p.Key} - {p.Value} orders{Trend(p.Key, p.Value)}");
        var reply = compareLabel is null
            ? $"📈 Trending ({periodLabel}):\n\n{string.Join("\n", lines)}"
            : $"📈 Trending {periodLabel}:\n\n{string.Join("\n", lines)}";
        var insight = await _ai.GenerateInsightAsync($"Top products ({periodLabel}): {string.Join(", ", top.Select(t => $"{t.Key} ({t.Value}{Trend(t.Key, t.Value)})"))}", ct);
        if (insight is not null) reply += $"\n\n🤖 Insight: {insight}";
        await ReplyAsync(seller, reply, ct);
    }

    private async Task<Dictionary<string, int>> ProductOrderCountsAsync(Seller seller, DateTime from, DateTime to, CancellationToken ct) =>
        await _db.OrderItems
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned && i.Order.CreatedAt >= from && i.Order.CreatedAt < to)
            .GroupBy(i => i.ProductNameSnapshot)
            .Select(g => new { Name = g.Key, Orders = g.Count() })
            .ToDictionaryAsync(g => g.Name, g => g.Orders, ct);

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

        var lines = slow.Select((p, i) => $"{i + 1}. {Formatters.ProductLabel(p)} - {Formatters.Money(p.Price)}");
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

        var lines = orders.Select((o, i) => $"{i + 1}. {o.Customer?.Name} - {Formatters.ItemsSummary(o)} - {Formatters.Money(OrderMoney.Balance(o))}" +
                                             (o.AmountPaid > 0 ? $" (advance {Formatters.Money(o.AmountPaid)} mila)" : "") +
                                             (o.DeliveredAt is { } d ? $" (delivered {Formatters.DaysAgo(d)})" : ""));
        var total = Formatters.Money(orders.Sum(OrderMoney.Balance));
        await ReplyAsync(seller, minDaysOld is int age
            ? $"💵 {age}+ Din Purane Unpaid ({orders.Count}):\n\n{string.Join("\n", lines)}\n\nTotal: {total} — follow-up karein."
            : $"💵 Delivered but Cash Not Collected ({orders.Count}):\n\n{string.Join("\n", lines)}\n\nTotal pending cash: {total}", ct);
    }
}
