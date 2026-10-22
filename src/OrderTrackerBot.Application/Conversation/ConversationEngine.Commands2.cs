using OrderTrackerBot.Application.Time;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private async Task HandleCancelOrderRequestAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == cmd.Number, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.028", cmd.Number), ct);
            return;
        }

        ctx.CancelOrderId = order.Id;
        SetState(session, ConversationState.AwaitingCancelConfirmation);
        await ReplyAsync(seller,
            MessageTemplates.Get("commands2.001", order.Id, order.Customer?.Name, Formatters.ItemsSummary(order)), ct);
    }

    private async Task HandleCancelConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        if (!CommandParser.IsAffirmative(message))
        {
            ctx.CancelOrderId = null;
            await ReplyAsync(seller, MessageTemplates.Get("commands2.002"), ct);
            return;
        }

        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == ctx.CancelOrderId && o.SellerId == seller.Id, ct);
        ctx.CancelOrderId = null;
        if (order is null) return;
        var stockBefore = StockFootprint(order);

        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id,
            ActionType = ActionType.OrderCancelled,
            OrderId = order.Id,
            PayloadJson = JsonSerializer.Serialize(new { PreviousStatus = order.Status.ToString() })
        });
        order.Status = OrderStatus.Cancelled;
        order.CancelledAt = DateTime.UtcNow;
        await ApplyStockChangeAsync(seller, stockBefore, StockFootprint(order), ct);
        await ReplyAsync(seller, MessageTemplates.Get("commands2.003", order.Id), ct);
    }

    private async Task HandleMarkAllPendingShippedAsync(Seller seller, ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        var ids = await _db.Orders.Where(o => o.SellerId == seller.Id && o.Status == OrderStatus.Pending)
            .Select(o => o.Id).ToListAsync(ct);

        if (ids.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.008"), ct);
            return;
        }

        ctx.BulkStatusOrderIds = ids;
        SetState(session, ConversationState.AwaitingBulkStatusConfirmation);
        await ReplyAsync(seller,
            MessageTemplates.Get("commands2.004", ids.Count, string.Join(", #", ids)), ct);
    }

    private async Task HandleBulkStatusConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        var ids = ctx.BulkStatusOrderIds ?? new List<int>();
        ctx.BulkStatusOrderIds = null;

        if (!CommandParser.IsAffirmative(message))
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.005"), ct);
            return;
        }

        var orders = await _db.Orders.Where(o => ids.Contains(o.Id) && o.SellerId == seller.Id).ToListAsync(ct);
        // Cancelled and returned orders are not shipped again (that would revive them without restoring their stock).
        var changed = 0;
        foreach (var order in orders.Where(o => o.Status is not (OrderStatus.Cancelled or OrderStatus.Returned)))
        {
            var stockBefore = StockFootprint(order);
            order.Status = OrderStatus.Shipped;
            order.ShippedAt = DateTime.UtcNow;
            await ApplyStockChangeAsync(seller, stockBefore, StockFootprint(order), ct);
            changed++;
        }

        await ReplyAsync(seller, MessageTemplates.Get("commands2.006", changed), ct);
    }

    private async Task HandleUndoAsync(Seller seller, CancellationToken ct)
    {
        var last = await _db.ActionLogs
            .Where(a => a.SellerId == seller.Id && !a.Undone)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (last is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.007"), ct);
            return;
        }

        last.Undone = true;

        switch (last.ActionType)
        {
            case ActionType.OrderCreated when last.OrderId is not null:
            {
                var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == last.OrderId && o.SellerId == seller.Id, ct);
                if (order is null) return;
                var stockBefore = StockFootprint(order);
                order.Status = OrderStatus.Cancelled;
                order.CancelledAt = DateTime.UtcNow;
                await ApplyStockChangeAsync(seller, stockBefore, StockFootprint(order), ct);
                await ReplyAsync(seller, MessageTemplates.Get("commands2.008", order.Id), ct);
                return;
            }
            case ActionType.OrderStatusChanged or ActionType.OrderCancelled when last.OrderId is not null:
            {
                var order = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
                    .FirstOrDefaultAsync(o => o.Id == last.OrderId, ct);
                if (order is null) return;

                using var doc = JsonDocument.Parse(last.PayloadJson);
                if (doc.RootElement.TryGetProperty("PreviousStatus", out var prevStatusEl))
                {
                    var stockBefore = StockFootprint(order);
                    order.Status = Enum.Parse<OrderStatus>(prevStatusEl.GetString()!);
                    await ApplyStockChangeAsync(seller, stockBefore, StockFootprint(order), ct);
                    await ReplyAsync(seller, MessageTemplates.Get("commands2.009", order.Id, order.Customer?.Name, Formatters.ItemsSummary(order), Formatters.Status(order.Status)), ct);
                }
                else if (doc.RootElement.TryGetProperty("PreviousPaymentStatus", out var prevPayEl))
                {
                    order.PaymentStatus = Enum.Parse<PaymentStatus>(prevPayEl.GetString()!);
                    if (order.PaymentStatus == PaymentStatus.Unpaid) order.PaidAt = null;
                    if (doc.RootElement.TryGetProperty("PreviousAmountPaid", out var prevAmountEl)) order.AmountPaid = prevAmountEl.GetDecimal();
                    await ReplyAsync(seller, MessageTemplates.Get("commands2.010", order.Id, order.PaymentStatus), ct);
                }
                return;
            }
            case ActionType.OrderEdited:
                await UndoOrderEditAsync(seller, last, ct);
                return;
            case ActionType.CustomerUpdated:
                await UndoCustomerUpdateAsync(seller, last, ct);
                return;
            case ActionType.ExpenseAdded:
                await UndoExpenseAsync(seller, last, ct);
                return;
            case ActionType.LossLogged:
                await UndoLossAsync(seller, last, ct);
                return;
            case ActionType.WageLogged:
                await UndoWageAsync(seller, last, ct);
                return;
            case ActionType.DataImported:
                await UndoDataImportAsync(seller, last, ct);
                return;
            case ActionType.ProductPriceChanged:
            {
                using var doc = JsonDocument.Parse(last.PayloadJson);
                var productId = doc.RootElement.GetProperty("ProductId").GetInt32();
                var oldPrice = doc.RootElement.GetProperty("OldPrice").GetDecimal();
                var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
                if (product is null) return;
                product.Price = oldPrice;
                await ReplyAsync(seller, MessageTemplates.Get("commands2.011", product.Name, Formatters.Money(oldPrice)), ct);
                return;
            }
            default:
                await ReplyAsync(seller, MessageTemplates.Get("commands2.012"), ct);
                return;
        }
    }

    private async Task HandlePaymentLinkAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        Order? order = cmd.Number is int n
            ? await _db.Orders.FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == n, ct)
            : await _db.Orders.Where(o => o.SellerId == seller.Id && o.PaymentStatus == PaymentStatus.Unpaid && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
                .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);

        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.013"), ct);
            return;
        }

        // Safepay is saved as a merchant ID only; no hosted checkout exists yet, so it is never
        // shown to the buyer as a payment number. Only JazzCash / Easypaisa / Bank are listed.
        var methods = (await _db.PaymentMethods.Where(p => p.SellerId == seller.Id).ToListAsync(ct))
            .Where(m => m.Type != SellerPaymentMethodType.Safepay)
            .ToList();
        if (methods.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.014"), ct);
            return;
        }

        var lines = methods.Select(m => $"{PaymentMethodName(m.Type)}: {m.AccountNumberOrId}");
        await ReplyAsync(seller,
            MessageTemplates.Get("commands2.016", order.Id, Formatters.Money(OrderMoney.Balance(order) is > 0 and var due ? due : order.Total), (order.AmountPaid > 0 && order.PaymentStatus != PaymentStatus.Paid ? $" (baqi; {Formatters.Money(order.AmountPaid)} advance mila)" : ""), string.Join("\n", lines), seller.BusinessName, order.Id), ct);
    }

    private static string PaymentMethodName(SellerPaymentMethodType type) => type switch
    {
        SellerPaymentMethodType.JazzCash => "JazzCash",
        SellerPaymentMethodType.Easypaisa => "Easypaisa",
        SellerPaymentMethodType.Bank => "Bank",
        _ => type.ToString()
    };

    private async Task HandleAddPaymentMethodAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var type = Enum.Parse<SellerPaymentMethodType>(cmd.Text!, ignoreCase: true);

        if (type == SellerPaymentMethodType.Safepay && cmd.Text2 is null)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("commands2.017"), ct);
            return;
        }

        if (cmd.Text2 is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.018", cmd.Text, cmd.Text), ct);
            return;
        }

        var existing = await _db.PaymentMethods.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Type == type, ct);
        if (existing is not null) existing.AccountNumberOrId = cmd.Text2;
        else _db.PaymentMethods.Add(new SellerPaymentMethod { SellerId = seller.Id, Type = type, AccountNumberOrId = cmd.Text2 });

        var count = await _db.PaymentMethods.CountAsync(p => p.SellerId == seller.Id, ct) + (existing is null ? 1 : 0);
        var others = new[] { "JazzCash", "Easypaisa", "Bank" }.Where(n => !n.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase));
        await ReplyAsync(seller,
            type == SellerPaymentMethodType.Safepay
                ? MessageTemplates.Get("commands2.044")
                : count == 1
                    ? MessageTemplates.Get("commands2.045", PaymentMethodName(type), string.Join(", ", others))
                    : MessageTemplates.Get("commands2.046", PaymentMethodName(type), count), ct);
    }

    private async Task HandleAddTrackingAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await _db.Orders.Where(o => o.SellerId == seller.Id && o.Status == OrderStatus.Shipped && o.TrackingNumber == null)
            .OrderByDescending(o => o.ShippedAt).FirstOrDefaultAsync(ct);

        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.019"), ct);
            return;
        }

        order.TrackingCourier = cmd.Text;
        order.TrackingNumber = cmd.Text2;
        await ReplyAsync(seller, MessageTemplates.Get("commands2.020", order.TrackingCourier, order.TrackingNumber), ct);
    }

    private async Task HandleTrackingLookupAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await FindLatestOrderByCustomerNameAsync(seller, cmd.Text!, ct);
        if (order is null || order.TrackingNumber is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.021", cmd.Text), ct);
            return;
        }

        await ReplyAsync(seller,
            MessageTemplates.Get("commands2.022", order.Customer?.Name, order.Id, order.TrackingCourier, order.TrackingNumber, Formatters.Status(order.Status)), ct);
    }

    private async Task HandleCustomerOrderLookupAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await FindLatestOrderByCustomerNameAsync(seller, cmd.Text!, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.023", cmd.Text), ct);
            return;
        }

        await ReplyAsync(seller,
            MessageTemplates.Get("commands2.024", order.Customer?.Name, Formatters.ItemsSummary(order), Formatters.Money(order.Total), Formatters.Status(order.Status), $"{order.CreatedAt:ddd, hh:mm tt}", order.Customer?.Phone, order.Customer?.Address), ct);
    }

    private async Task HandleFuzzyStatusUpdateAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await FindLatestOrderByCustomerNameAsync(seller, cmd.Text!, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.023", cmd.Text), ct);
            return;
        }

        var keyword = cmd.Text2!.StartsWith("deliver") ? "delivered" : cmd.Text2.StartsWith("ship") ? "shipped"
            : cmd.Text2 == "return" ? "returned" : "pending";
        await ApplyStatusChangeAsync(seller, session, ctx, order, keyword, ct);
    }

    private Task<Order?> FindLatestOrderByCustomerNameAsync(Seller seller, string name, CancellationToken ct) =>
        _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.Customer!.Name.ToLower() == name.ToLower())
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct)!;

    private static readonly Regex DiscountSpec = new(
        @"^(?<code>\S+)\s*,\s*(?:(?<percent>\d+(?:\.\d+)?)\s*(?:percent|%|pc)|Rs\.?\s*(?<flat>\d+(?:\.\d+)?)(?:\s*flat)?|(?<flat>\d+(?:\.\d+)?)\s*(?:rs|rupees?|flat))\s*(?:,\s*(?<exp>.+?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DiscountCodeOnly = new(@"^[A-Za-z0-9_-]{3,20}$", RegexOptions.Compiled);

    // Guided flow: "add discount" -> code -> value. Accepts a full spec at any step, and lets cancel words / commands out.
    private async Task HandleDiscountDetailsAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var trimmed = message.Trim();
        var command = CommandParser.TryParse(trimmed);
        var isCancel = CancelWords.Contains(trimmed);
        if (isCancel || (command is not null && command.Kind is not (CommandKind.AddProduct or CommandKind.AddProductsBulk)))
        {
            ctx.PendingDiscountCode = null;
            SetState(session, ConversationState.Idle);
            if (isCancel)
            {
                await ReplyAsync(seller, MessageTemplates.Get("commands2.025"), ct);
                return;
            }
            await ExecuteCommandAsync(seller, session, ctx, command!, ct);
            return;
        }

        var spec = CommandParser.NormalizeDiscountSpec(ctx.PendingDiscountCode is null ? trimmed : $"{ctx.PendingDiscountCode}, {trimmed}");
        if (DiscountSpec.IsMatch(spec))
        {
            ctx.PendingDiscountCode = null;
            SetState(session, ConversationState.Idle);
            await HandleCreateDiscountAsync(seller, new ParsedCommand { Kind = CommandKind.CreateDiscount, Text = spec }, ct);
            return;
        }

        if (ctx.PendingDiscountCode is null && DiscountCodeOnly.IsMatch(trimmed))
        {
            ctx.PendingDiscountCode = trimmed.ToUpperInvariant();
            await ReplyAsync(seller,
                MessageTemplates.Get("commands2.026", ctx.PendingDiscountCode), ct);
            return;
        }

        await ReplyAsync(seller,
            ctx.PendingDiscountCode is null
                ? MessageTemplates.Get("commands2.047")
                : MessageTemplates.Get("commands2.048", ctx.PendingDiscountCode), ct);
    }

    private async Task HandleCreateDiscountAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var match = DiscountSpec.Match(CommandParser.NormalizeDiscountSpec(cmd.Text!));
        if (!match.Success)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.027"), ct);
            return;
        }

        var isPercent = match.Groups["percent"].Success;
        var value = decimal.Parse(isPercent ? match.Groups["percent"].Value : match.Groups["flat"].Value);
        DateTime? expiresAt = null;
        if (match.Groups["exp"].Success)
        {
            if (!ReportPeriods.TryParseExpiry(match.Groups["exp"].Value, DateTime.UtcNow, seller.TimeZoneId, out var expiry))
            {
                await ReplyAsync(seller, MessageTemplates.Get("commands2.028"), ct);
                return;
            }
            expiresAt = expiry;
        }

        if (isPercent && value is <= 0 or > 100)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.029"), ct);
            return;
        }
        if (!isPercent && value <= 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.030"), ct);
            return;
        }

        var code = match.Groups["code"].Value.ToUpperInvariant();
        if (await _db.Discounts.AnyAsync(d => d.SellerId == seller.Id && d.Code == code, ct))
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.031", code), ct);
            return;
        }
        _db.Discounts.Add(new Discount
        {
            SellerId = seller.Id,
            Code = code,
            Type = isPercent ? DiscountType.Percent : DiscountType.Flat,
            Value = value,
            ExpiresAt = expiresAt
        });

        var valueText = isPercent ? $"{value}% off" : $"{Formatters.Money(value)} flat off";
        var reply = expiresAt is null
            ? MessageTemplates.Get("commands2.049", code, valueText)
            : MessageTemplates.Get("commands2.050", code, valueText, (expiresAt.Value - DateTime.UtcNow).Days, $"{expiresAt:dd MMM}");
        await ReplyAsync(seller, reply, ct);
    }

    private async Task StartResetAsync(Seller seller, ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        SetState(session, ConversationState.AwaitingResetConfirmation);
        ctx.ResetAskedAt = _clock.GetUtcNow().UtcDateTime;
        ctx.ResetGenerationId = Guid.NewGuid().ToString("N");
        await ReplyAsync(seller,
            MessageTemplates.Get("commands2.032"), ct);
    }

    private async Task HandleResetConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var asked = ctx.ResetAskedAt;
        var generation = ctx.ResetGenerationId;
        ctx.ResetAskedAt = null;
        ctx.ResetGenerationId = null;
        SetState(session, ConversationState.Idle);

        if (!CommandParser.IsAffirmative(message))
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.033"), ct);
            return;
        }
        if (!ResetConfirmation.IsValid(asked, generation, _clock.GetUtcNow().UtcDateTime))
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.034"), ct);
            return;
        }

        // Claim before wiping. The claim commits on its own, so a crash after it leaves the account untouched and the seller can ask again.
        if (!await ResetConfirmation.TryClaimAsync(_db, session.Id, generation!, ct))
        {
            // Another instance won the claim and has already moved the session on. Put back exactly what this turn loaded, so the
            // save at the end of the turn writes nothing over the winner's state (onboarding) or context.
            ctx.ResetAskedAt = asked;
            ctx.ResetGenerationId = generation;
            SetState(session, ConversationState.AwaitingResetConfirmation);
            await ReplyAsync(seller, MessageTemplates.Get("commands2.035"), ct);
            return;
        }

        // The wipe is one SaveChanges, so it either fully applies or leaves the data as it was.
        await SellerAccountReset.ResetAsync(_db, seller, ct);
        await _db.SaveChangesAsync(ct); // persist before telling the seller it worked
        await ReplyAsync(seller, MessageTemplates.Get("commands2.036"), ct);
        await StartOnboardingAsync(seller, session, ct);
    }

    private async Task HandleCustomerFeedbackListAsync(Seller seller, CancellationToken ct)
    {
        var items = await _db.CustomerFeedbacks
            .Where(f => f.SellerId == seller.Id)
            .OrderByDescending(f => f.CreatedAt)
            .Take(10)
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.037"), ct);
            return;
        }

        static string Emoji(string? s) => s switch { "positive" => " 😊", "negative" => " 😞", "neutral" => " 😐", _ => "" };
        var lines = items.Select((f, i) =>
            $"{i + 1}. {f.CustomerName ?? "Customer"}{(f.OrderId is null ? "" : $" (#{f.OrderId})")} - \"{f.Text}\"{Emoji(f.Sentiment)}");
        var positive = items.Count(f => f.Sentiment == "positive");
        var negative = items.Count(f => f.Sentiment == "negative");
        var overall = positive + negative == 0 ? "" : positive >= negative
            ? MessageTemplates.Get("commands2.051")
            : MessageTemplates.Get("commands2.052");
        await ReplyAsync(seller, MessageTemplates.Get("commands2.038", string.Join("\n", lines), overall), ct);
    }

    private async Task HandleDiscountListAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        var discounts = await _db.Discounts
            .Where(d => d.SellerId == seller.Id && d.IsActive && (d.ExpiresAt == null || d.ExpiresAt > DateTime.UtcNow))
            .ToListAsync(ct);

        if (discounts.Count == 0)
        {
            SetState(session, ConversationState.AwaitingDiscountDetails);
            await ReplyAsync(seller, MessageTemplates.Get("commands2.039", HowToText("discount")), ct);
            return;
        }

        var lines = discounts.Select((d, i) =>
        {
            var value = d.Type == DiscountType.Percent ? $"{d.Value}%" : Formatters.Money(d.Value) + " flat";
            var expiry = d.ExpiresAt is null ? "no expiry" : $"expires {d.ExpiresAt:dd MMM}";
            return $"{i + 1}. {d.Code} - {value} - {expiry}";
        });
        await ReplyAsync(seller, MessageTemplates.Get("commands2.040", string.Join("\n", lines)), ct);
    }

    private async Task HandleCreateLoyaltyAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        _db.LoyaltyRules.Add(new LoyaltyRule { SellerId = seller.Id, OrderThreshold = cmd.Number!.Value, DiscountPercent = cmd.Amount!.Value });
        await ReplyAsync(seller,
            MessageTemplates.Get("commands2.041", cmd.Number, cmd.Amount), ct);
    }

    private async Task HandleLoyalCustomersAsync(Seller seller, CancellationToken ct)
    {
        // Aggregated client-side: Sqlite (dev/docker provider) can't SUM a decimal column in SQL.
        var rows = await _db.Orders
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.DeletedAt == null)
            .Select(o => new { o.CustomerId, o.Customer!.Name, o.Total })
            .ToListAsync(ct);
        var top = rows
            .GroupBy(o => new { o.CustomerId, o.Name })
            .Select(g => new { g.Key.Name, Orders = g.Count(), Total = g.Sum(o => o.Total) })
            .OrderByDescending(g => g.Orders).ThenByDescending(g => g.Total)
            .Take(5)
            .ToList();

        if (top.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands2.042"), ct);
            return;
        }

        var lines = top.Select((c, i) => $"{i + 1}. {c.Name} - {c.Orders} orders - {Formatters.Money(c.Total)}");
        var reply = MessageTemplates.Get("commands2.054", string.Join("\n", lines));
        if (top.Count > 0)
            reply += $"\n\n🌟 Tip: {top[0].Name} ko discount code bhej kar retain karein — \"create discount\" try karein.";
        await ReplyAsync(seller, reply, ct);
    }

    private async Task HandleFeedbackAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        _db.MerchantFeedbacks.Add(new MerchantFeedback { SellerId = seller.Id, Text = cmd.Text! });
        await ReplyAsync(seller, MessageTemplates.Get("commands2.043"), ct);
        await _founderAlerts.NotifyAsync(seller.Id, $"[{seller.BusinessName}] {cmd.Text}", ct);
    }
}
