using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private async Task HandleCancelOrderRequestAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == cmd.Number, ct);
        if (order is null)
        {
            await ReplyAsync(seller, $"Order #{cmd.Number} nahi mila.", ct);
            return;
        }

        ctx.CancelOrderId = order.Id;
        SetState(session, ConversationState.AwaitingCancelConfirmation);
        await ReplyAsync(seller,
            $"⚠️ Order #{order.Id} ({order.Customer?.Name}, {Formatters.ItemsSummary(order)}) cancel karna confirm karein?\nReply YES to cancel.", ct);
    }

    private async Task HandleCancelConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        if (!CommandParser.IsAffirmative(message))
        {
            ctx.CancelOrderId = null;
            await ReplyAsync(seller, "Theek hai, cancel nahi kiya.", ct);
            return;
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == ctx.CancelOrderId && o.SellerId == seller.Id, ct);
        ctx.CancelOrderId = null;
        if (order is null) return;

        order.Status = OrderStatus.Cancelled;
        order.CancelledAt = DateTime.UtcNow;
        await ReplyAsync(seller, $"✅ Order #{order.Id} CANCELLED.", ct);
    }

    private async Task HandleMarkAllPendingShippedAsync(Seller seller, ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        var ids = await _db.Orders.Where(o => o.SellerId == seller.Id && o.Status == OrderStatus.Pending)
            .Select(o => o.Id).ToListAsync(ct);

        if (ids.Count == 0)
        {
            await ReplyAsync(seller, "Koi pending order nahi hai.", ct);
            return;
        }

        ctx.BulkStatusOrderIds = ids;
        SetState(session, ConversationState.AwaitingBulkStatusConfirmation);
        await ReplyAsync(seller,
            $"⚠️ {ids.Count} pending orders shipped mark karna confirm karein? (#{string.Join(", #", ids)})\nReply YES.", ct);
    }

    private async Task HandleBulkStatusConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        var ids = ctx.BulkStatusOrderIds ?? new List<int>();
        ctx.BulkStatusOrderIds = null;

        if (!CommandParser.IsAffirmative(message))
        {
            await ReplyAsync(seller, "Theek hai, kuch update nahi hua.", ct);
            return;
        }

        var orders = await _db.Orders.Where(o => ids.Contains(o.Id) && o.SellerId == seller.Id).ToListAsync(ct);
        foreach (var order in orders)
        {
            order.Status = OrderStatus.Shipped;
            order.ShippedAt = DateTime.UtcNow;
        }

        await ReplyAsync(seller, $"✅ {orders.Count} orders SHIPPED mark ho gaye.", ct);
    }

    private async Task HandleUndoAsync(Seller seller, CancellationToken ct)
    {
        var last = await _db.ActionLogs
            .Where(a => a.SellerId == seller.Id && !a.Undone)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (last is null)
        {
            await ReplyAsync(seller, "Undo karne ke liye kuch nahi hai.", ct);
            return;
        }

        last.Undone = true;

        switch (last.ActionType)
        {
            case ActionType.OrderStatusChanged when last.OrderId is not null:
            {
                var order = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
                    .FirstOrDefaultAsync(o => o.Id == last.OrderId, ct);
                if (order is null) return;

                using var doc = JsonDocument.Parse(last.PayloadJson);
                if (doc.RootElement.TryGetProperty("PreviousStatus", out var prevStatusEl))
                {
                    order.Status = Enum.Parse<OrderStatus>(prevStatusEl.GetString()!);
                    await ReplyAsync(seller, $"↩️ Reverted — Order #{order.Id} ({order.Customer?.Name} - {Formatters.ItemsSummary(order)}) back to {Formatters.Status(order.Status)}.", ct);
                }
                else if (doc.RootElement.TryGetProperty("PreviousPaymentStatus", out var prevPayEl))
                {
                    order.PaymentStatus = Enum.Parse<PaymentStatus>(prevPayEl.GetString()!);
                    await ReplyAsync(seller, $"↩️ Reverted — Order #{order.Id} payment status back to {order.PaymentStatus}.", ct);
                }
                return;
            }
            case ActionType.ProductPriceChanged:
            {
                using var doc = JsonDocument.Parse(last.PayloadJson);
                var productId = doc.RootElement.GetProperty("ProductId").GetInt32();
                var oldPrice = doc.RootElement.GetProperty("OldPrice").GetDecimal();
                var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
                if (product is null) return;
                product.Price = oldPrice;
                await ReplyAsync(seller, $"↩️ Reverted — {product.Name} price back to {Formatters.Money(oldPrice)}.", ct);
                return;
            }
            default:
                await ReplyAsync(seller, "↩️ Reverted.", ct);
                return;
        }
    }

    private async Task HandlePaymentLinkAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        Order? order = cmd.Number is int n
            ? await _db.Orders.FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == n, ct)
            : await _db.Orders.Where(o => o.SellerId == seller.Id && o.PaymentStatus == PaymentStatus.Unpaid && o.Status != OrderStatus.Cancelled)
                .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);

        if (order is null)
        {
            await ReplyAsync(seller, "Koi unpaid order nahi mila.", ct);
            return;
        }

        var methods = await _db.PaymentMethods.Where(p => p.SellerId == seller.Id).ToListAsync(ct);
        if (methods.Count == 0)
        {
            await ReplyAsync(seller, "Pehle payment method setup karein: \"add payment: jazzcash, number\"", ct);
            return;
        }

        if (methods.Any(m => m.Type == SellerPaymentMethodType.Safepay))
        {
            await ReplyAsync(seller,
                "💳 Payment link banaya gaya (Safepay integration is a roadmap item — wiring the real hosted-checkout call is a follow-up task).\n" +
                $"Amount: {Formatters.Money(order.Total)}\nOrder: #{order.Id}", ct);
            return;
        }

        var lines = methods.Select(m => $"{m.Type}: {m.AccountNumberOrId}");
        await ReplyAsync(seller,
            $"💰 Payment details for Order #{order.Id}:\n\n" +
            $"Amount: {Formatters.Money(order.Total)}\n{string.Join("\n", lines)}\n({seller.BusinessName})\n\n" +
            $"Customer ko bhej dein. Payment hone par \"mark {order.Id} paid\" likhein.", ct);
    }

    private async Task HandleAddPaymentMethodAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var type = Enum.Parse<SellerPaymentMethodType>(cmd.Text!, ignoreCase: true);

        if (type == SellerPaymentMethodType.Safepay && cmd.Text2 is null)
        {
            await ReplyAsync(seller,
                "💳 Safepay se automatic payment links banane ke liye:\n\n" +
                "1. safepay.pk par apna merchant account banayein\n" +
                "2. Apna Merchant ID copy karein\n" +
                "3. Yahan bhejein: \"safepay id: [your-id]\"", ct);
            return;
        }

        if (cmd.Text2 is null)
        {
            await ReplyAsync(seller, $"\"{cmd.Text}\" ka account number/ID bhi bhejein: \"add payment: {cmd.Text}, number\"", ct);
            return;
        }

        var existing = await _db.PaymentMethods.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Type == type, ct);
        if (existing is not null) existing.AccountNumberOrId = cmd.Text2;
        else _db.PaymentMethods.Add(new SellerPaymentMethod { SellerId = seller.Id, Type = type, AccountNumberOrId = cmd.Text2 });

        var count = await _db.PaymentMethods.CountAsync(p => p.SellerId == seller.Id, ct) + (existing is null ? 1 : 0);
        await ReplyAsync(seller,
            type == SellerPaymentMethodType.Safepay
                ? "✅ Safepay connect ho gaya."
                : $"✅ {type} saved. Total {count} payment methods active.", ct);
    }

    private async Task HandleAddTrackingAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await _db.Orders.Where(o => o.SellerId == seller.Id && o.Status == OrderStatus.Shipped && o.TrackingNumber == null)
            .OrderByDescending(o => o.ShippedAt).FirstOrDefaultAsync(ct);

        if (order is null)
        {
            await ReplyAsync(seller, "Koi shipped order nahi mila jispe tracking add karni ho.", ct);
            return;
        }

        order.TrackingCourier = cmd.Text;
        order.TrackingNumber = cmd.Text2;
        await ReplyAsync(seller, $"✅ Tracking saved: {order.TrackingCourier} {order.TrackingNumber}", ct);
    }

    private async Task HandleTrackingLookupAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await FindLatestOrderByCustomerNameAsync(seller, cmd.Text!, ct);
        if (order is null || order.TrackingNumber is null)
        {
            await ReplyAsync(seller, $"{cmd.Text} ka tracking abhi available nahi hai.", ct);
            return;
        }

        await ReplyAsync(seller,
            $"📦 {order.Customer?.Name}'s Order #{order.Id}:\nCourier: {order.TrackingCourier}\nTracking: {order.TrackingNumber}\nStatus: {Formatters.Status(order.Status)}", ct);
    }

    private async Task HandleCustomerOrderLookupAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await FindLatestOrderByCustomerNameAsync(seller, cmd.Text!, ct);
        if (order is null)
        {
            await ReplyAsync(seller, $"{cmd.Text} ka koi order nahi mila.", ct);
            return;
        }

        await ReplyAsync(seller,
            $"🔍 {order.Customer?.Name}'s latest order:\n\n" +
            $"{Formatters.ItemsSummary(order)} - {Formatters.Money(order.Total)}\n" +
            $"Status: {Formatters.Status(order.Status)}\n" +
            $"Ordered: {order.CreatedAt:ddd, hh:mm tt}\n" +
            $"Phone: {order.Customer?.Phone}\n" +
            $"Address: {order.Customer?.Address}", ct);
    }

    private async Task HandleFuzzyStatusUpdateAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await FindLatestOrderByCustomerNameAsync(seller, cmd.Text!, ct);
        if (order is null)
        {
            await ReplyAsync(seller, $"{cmd.Text} ka koi order nahi mila.", ct);
            return;
        }

        var keyword = cmd.Text2!.StartsWith("deliver") ? "delivered" : cmd.Text2.StartsWith("ship") ? "shipped" : "pending";
        await ApplyStatusChangeAsync(seller, session, ctx, order, keyword, ct);
    }

    private Task<Order?> FindLatestOrderByCustomerNameAsync(Seller seller, string name, CancellationToken ct) =>
        _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.Customer!.Name.ToLower() == name.ToLower())
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct)!;

    private static readonly Regex DiscountSpec = new(
        @"^(?<code>\S+)\s*,\s*(?:(?<percent>\d+(?:\.\d+)?)\s*percent|Rs\.?\s*(?<flat>\d+(?:\.\d+)?)\s*flat)\s*(?:,\s*expires\s+(?<days>\d+)\s*days?)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task HandleCreateDiscountAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var match = DiscountSpec.Match(cmd.Text!);
        if (!match.Success)
        {
            await ReplyAsync(seller, "Format: \"create discount: CODE, 10 percent[, expires 15 days]\" ya \"CODE, Rs.50 flat\"", ct);
            return;
        }

        var isPercent = match.Groups["percent"].Success;
        var value = decimal.Parse(isPercent ? match.Groups["percent"].Value : match.Groups["flat"].Value);
        DateTime? expiresAt = match.Groups["days"].Success ? DateTime.UtcNow.AddDays(int.Parse(match.Groups["days"].Value)) : null;

        var code = match.Groups["code"].Value.ToUpperInvariant();
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
            ? $"✅ Discount code {code} created — {valueText}."
            : $"✅ Discount code {code} created:\n{valueText}, expires in {(expiresAt.Value - DateTime.UtcNow).Days} days ({expiresAt:dd MMM})";
        await ReplyAsync(seller, reply, ct);
    }

    private async Task HandleDiscountListAsync(Seller seller, CancellationToken ct)
    {
        var discounts = await _db.Discounts
            .Where(d => d.SellerId == seller.Id && d.IsActive && (d.ExpiresAt == null || d.ExpiresAt > DateTime.UtcNow))
            .ToListAsync(ct);

        if (discounts.Count == 0)
        {
            await ReplyAsync(seller, "Koi active discount nahi hai.", ct);
            return;
        }

        var lines = discounts.Select((d, i) =>
        {
            var value = d.Type == DiscountType.Percent ? $"{d.Value}%" : Formatters.Money(d.Value) + " flat";
            var expiry = d.ExpiresAt is null ? "no expiry" : $"expires {d.ExpiresAt:dd MMM}";
            return $"{i + 1}. {d.Code} - {value} - {expiry}";
        });
        await ReplyAsync(seller, $"🎟️ Active Discounts:\n{string.Join("\n", lines)}", ct);
    }

    private async Task HandleCreateLoyaltyAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        _db.LoyaltyRules.Add(new LoyaltyRule { SellerId = seller.Id, OrderThreshold = cmd.Number!.Value, DiscountPercent = cmd.Amount!.Value });
        await ReplyAsync(seller,
            $"✅ Loyalty rule set:\nHar customer jo {cmd.Number} orders complete karega, unhe {cmd.Amount}% discount mil sakta hai.\n\n" +
            "Jab bhi koi customer threshold cross karega, main aapko batao ga.", ct);
    }

    private async Task HandleLoyalCustomersAsync(Seller seller, CancellationToken ct)
    {
        var top = await _db.Orders
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled)
            .GroupBy(o => new { o.CustomerId, o.Customer!.Name })
            .Select(g => new { g.Key.Name, Orders = g.Count(), Total = g.Sum(o => o.Total) })
            .OrderByDescending(g => g.Orders)
            .Take(5)
            .ToListAsync(ct);

        if (top.Count == 0)
        {
            await ReplyAsync(seller, "Abhi koi customer data nahi hai.", ct);
            return;
        }

        var lines = top.Select((c, i) => $"{i + 1}. {c.Name} - {c.Orders} orders - {Formatters.Money(c.Total)}");
        var reply = $"⭐ Top Customers:\n\n{string.Join("\n", lines)}";
        if (top.Count > 0)
            reply += $"\n\n🌟 Tip: {top[0].Name} ko discount code bhej kar retain karein — \"create discount\" try karein.";
        await ReplyAsync(seller, reply, ct);
    }

    private async Task HandleFeedbackAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        _db.MerchantFeedbacks.Add(new MerchantFeedback { SellerId = seller.Id, Text = cmd.Text! });
        await ReplyAsync(seller, "🙏 Shukriya! Aapka feedback humein mil gaya — hum jald improve karenge.", ct);
        await _founderAlerts.NotifyAsync(seller.Id, $"[{seller.BusinessName}] {cmd.Text}", ct);
    }
}
