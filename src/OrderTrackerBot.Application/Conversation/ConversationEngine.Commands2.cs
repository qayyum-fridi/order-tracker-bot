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

        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id,
            ActionType = ActionType.OrderCancelled,
            OrderId = order.Id,
            PayloadJson = JsonSerializer.Serialize(new { PreviousStatus = order.Status.ToString() })
        });
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
            case ActionType.OrderCreated when last.OrderId is not null:
            {
                var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == last.OrderId && o.SellerId == seller.Id, ct);
                if (order is null) return;
                order.Status = OrderStatus.Cancelled;
                order.CancelledAt = DateTime.UtcNow;
                await ReplyAsync(seller, $"↩️ Reverted — Order #{order.Id} hata diya (CANCELLED).", ct);
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
                    order.Status = Enum.Parse<OrderStatus>(prevStatusEl.GetString()!);
                    await ReplyAsync(seller, $"↩️ Reverted — Order #{order.Id} ({order.Customer?.Name} - {Formatters.ItemsSummary(order)}) back to {Formatters.Status(order.Status)}.", ct);
                }
                else if (doc.RootElement.TryGetProperty("PreviousPaymentStatus", out var prevPayEl))
                {
                    order.PaymentStatus = Enum.Parse<PaymentStatus>(prevPayEl.GetString()!);
                    if (order.PaymentStatus == PaymentStatus.Unpaid) order.PaidAt = null;
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

        var lines = methods.Select(m => $"{PaymentMethodName(m.Type)}: {m.AccountNumberOrId}");
        await ReplyAsync(seller,
            $"💰 Payment details for Order #{order.Id}:\n\n" +
            $"Amount: {Formatters.Money(order.Total)}\n{string.Join("\n", lines)}\n({seller.BusinessName})\n\n" +
            $"Customer ko bhej dein. Payment hone par \"mark {order.Id} paid\" likhein.", ct);
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
                "💳 Safepay se automatic payment links banane ke liye:\n\n" +
                "1. safepay.pk par apna merchant account banayein (5 min)\n" +
                "2. Apna Merchant ID copy karein\n" +
                "3. Yahan bhejein: \"safepay id: [your-id]\"\n\n" +
                "Account nahi bana? Link: safepay.pk/signup", ct);
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
        var others = new[] { "JazzCash", "Easypaisa", "Bank" }.Where(n => !n.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase));
        await ReplyAsync(seller,
            type == SellerPaymentMethodType.Safepay
                ? "✅ Safepay ID save ho gayi. (Automatic payment links ka integration jald aa raha hai — tab tak \"payment link\" aapke manual numbers dikhayega.)"
                : count == 1
                    ? $"✅ {PaymentMethodName(type)} number saved.\n\"payment link\" command ab customer ko yeh number dikhayega.\n\nAur payment method add karna hai? ({string.Join(", ", others)})"
                    : $"✅ {PaymentMethodName(type)} bhi saved. Total {count} payment methods active.", ct);
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
        @"^(?<code>\S+)\s*,\s*(?:(?<percent>\d+(?:\.\d+)?)\s*(?:percent|%|pc)|Rs\.?\s*(?<flat>\d+(?:\.\d+)?)(?:\s*flat)?|(?<flat>\d+(?:\.\d+)?)\s*(?:rs|rupees?|flat))\s*(?:,\s*expires\s+(?<days>\d+)\s*days?)?$",
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
                await ReplyAsync(seller, "Theek hai, discount nahi banaya.", ct);
                return;
            }
            await ExecuteCommandAsync(seller, session, ctx, command!, ct);
            return;
        }

        var spec = ctx.PendingDiscountCode is null ? trimmed : $"{ctx.PendingDiscountCode}, {trimmed}";
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
                $"👍 Code: {ctx.PendingDiscountCode}\n\nAb kitna discount? Likhein:\n10 percent\nya\nRs.50 flat\n\n(Expiry chahiye to: 10 percent, expires 15 days)", ct);
            return;
        }

        await ReplyAsync(seller,
            ctx.PendingDiscountCode is null
                ? "Pehle discount code ka naam bhejein (e.g. EID10), ya \"cancel\" likhein."
                : $"Code {ctx.PendingDiscountCode} ke liye value likhein: \"10 percent\" ya \"Rs.50 flat\", ya \"cancel\" likhein.", ct);
    }

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

    private async Task StartResetAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.AwaitingResetConfirmation);
        await ReplyAsync(seller,
            "⚠️ Yeh aapka poora data (orders, products, customers, discounts) delete kar dega aur setup dobara shuru hoga.\nReply YES to confirm, ya koi bhi aur message cancel karne ke liye.", ct);
    }

    private async Task HandleResetConfirmationAsync(Seller seller, ConversationSession session, string message, CancellationToken ct)
    {
        if (!CommandParser.IsAffirmative(message))
        {
            SetState(session, ConversationState.Idle);
            await ReplyAsync(seller, "Theek hai, kuch delete nahi kiya.", ct);
            return;
        }

        _db.Orders.RemoveRange(await _db.Orders.Where(o => o.SellerId == seller.Id).ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
        _db.Customers.RemoveRange(await _db.Customers.Where(c => c.SellerId == seller.Id).ToListAsync(ct));
        _db.Products.RemoveRange(await _db.Products.Where(p => p.SellerId == seller.Id).ToListAsync(ct));
        _db.Discounts.RemoveRange(await _db.Discounts.Where(d => d.SellerId == seller.Id).ToListAsync(ct));
        _db.LoyaltyRules.RemoveRange(await _db.LoyaltyRules.Where(l => l.SellerId == seller.Id).ToListAsync(ct));
        _db.PaymentMethods.RemoveRange(await _db.PaymentMethods.Where(p => p.SellerId == seller.Id).ToListAsync(ct));
        _db.ActionLogs.RemoveRange(await _db.ActionLogs.Where(a => a.SellerId == seller.Id).ToListAsync(ct));
        _db.MerchantFeedbacks.RemoveRange(await _db.MerchantFeedbacks.Where(f => f.SellerId == seller.Id).ToListAsync(ct));
        _db.CustomerFeedbacks.RemoveRange(await _db.CustomerFeedbacks.Where(f => f.SellerId == seller.Id).ToListAsync(ct));

        seller.BusinessName = null;
        seller.OnboardingComplete = false;
        seller.PreferredLanguage = Lang.RomanUrdu;
        await ReplyAsync(seller, "✅ Account reset ho gaya.", ct);
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
            await ReplyAsync(seller, "Abhi tak koi customer feedback save nahi hua.", ct);
            return;
        }

        static string Emoji(string? s) => s switch { "positive" => " 😊", "negative" => " 😞", "neutral" => " 😐", _ => "" };
        var lines = items.Select((f, i) =>
            $"{i + 1}. {f.CustomerName ?? "Customer"}{(f.OrderId is null ? "" : $" (#{f.OrderId})")} - \"{f.Text}\"{Emoji(f.Sentiment)}");
        var positive = items.Count(f => f.Sentiment == "positive");
        var negative = items.Count(f => f.Sentiment == "negative");
        var overall = positive + negative == 0 ? "" : positive >= negative
            ? "\n\nOverall sentiment: Mostly positive"
            : "\n\nOverall sentiment: Kuch customers naraz hain — follow-up karein";
        await ReplyAsync(seller, $"💬 Recent Customer Feedback:\n\n{string.Join("\n", lines)}{overall}", ct);
    }

    private async Task HandleDiscountListAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        var discounts = await _db.Discounts
            .Where(d => d.SellerId == seller.Id && d.IsActive && (d.ExpiresAt == null || d.ExpiresAt > DateTime.UtcNow))
            .ToListAsync(ct);

        if (discounts.Count == 0)
        {
            SetState(session, ConversationState.AwaitingDiscountDetails);
            await ReplyAsync(seller, "Abhi koi active discount nahi hai.\n\n" + HowToText("discount"), ct);
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
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Customer!.DeletedAt == null)
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
