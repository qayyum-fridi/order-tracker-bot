using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private static readonly string[] DefaultClarificationOptions =
    {
        "Naya order add karna chahte hain",
        "Kisi order ka status update karna chahte hain"
    };

    private async Task HandleFreeformMessageAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var catalog = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive)
            .Select(p => new AiCatalogItem { Name = p.Name, Price = p.Price })
            .ToListAsync(ct);

        var analysis = await _ai.AnalyzeMessageAsync(
            new AiAnalysisContext { BusinessName = seller.BusinessName ?? "", Catalog = catalog, PreferredLanguage = seller.PreferredLanguage },
            message, ct);

        if (!analysis.IsOrderAttempt || analysis.Order is null)
        {
            var options = analysis.ClarificationOptions.Count > 0 ? analysis.ClarificationOptions : DefaultClarificationOptions.ToList();
            ctx.ClarificationOptions = options;
            SetState(session, ConversationState.AwaitingClarificationChoice);
            var question = analysis.ClarificationQuestion ?? "Mujhe samajh nahi aaya 🤔 Kya aap:";
            var numbered = string.Join("\n", options.Select((o, i) => $"{i + 1}️⃣ {o}"));
            await ReplyAsync(seller, $"{question}\n\n{numbered}\n\nReply number se, ya \"help\" likhein.", ct);
            return;
        }

        if (analysis.IsAmbiguousItemGrouping && analysis.Order.Items.Count >= 2)
        {
            ctx.PendingOrder = BuildPendingOrder(analysis.Order, catalog);
            SetState(session, ConversationState.AwaitingOrderGroupingChoice);
            await ReplyAsync(seller,
                "Thoda confusion hai — 2 alag orders (har item alag) ya ek order mein dono?\n\n1️⃣ 2 separate orders\n2️⃣ 1 order, saare items", ct);
            return;
        }

        ctx.PendingOrder = BuildPendingOrder(analysis.Order, catalog);
        await ContinueResolvingDraftAsync(seller, session, ctx, ct);
    }

    private static PendingOrderData BuildPendingOrder(AiOrderDraft draft, List<AiCatalogItem> catalog)
    {
        var pending = new PendingOrderData
        {
            CustomerName = draft.CustomerName,
            Phone = draft.Phone,
            Address = draft.Address,
            PaymentMethodText = draft.PaymentMethod,
            DiscountCode = draft.DiscountCode
        };

        foreach (var item in draft.Items)
        {
            var matchName = item.MatchedCatalogProductName ?? item.ProductName;
            var product = catalog.FirstOrDefault(c => string.Equals(c.Name, matchName, StringComparison.OrdinalIgnoreCase));
            pending.Items.Add(new PendingOrderItemData
            {
                ProductName = product?.Name ?? item.ProductName,
                Quantity = item.Quantity <= 0 ? 1 : item.Quantity,
                UnitPrice = product?.Price ?? -1m // -1 = unresolved, not yet in catalog
            });
        }

        return pending;
    }

    /// <summary>
    /// Re-entrant: called after building a fresh draft, and again after each missing piece
    /// (a field, an unresolved product, a grouping choice) is filled in. Walks the remaining
    /// gaps in priority order until the draft is ready to save or confirm.
    /// </summary>
    private async Task ContinueResolvingDraftAsync(Seller seller, ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        var pending = ctx.PendingOrder!;

        var unresolved = pending.Items.FirstOrDefault(i => i.UnitPrice < 0);
        if (unresolved is not null)
        {
            ctx.PendingNewProductName = unresolved.ProductName;
            ctx.PendingMissingField = "ProductChoice";
            SetState(session, ConversationState.AwaitingOrderMissingFields);
            await ReplyAsync(seller,
                $"\"{unresolved.ProductName}\" aapke catalog mein nahi mila.\n\n" +
                "1️⃣ Ye naya product hai — catalog mein add karoon?\n" +
                "2️⃣ Ye kisi existing product ka doosra naam hai (batayein kaunsa)", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(pending.CustomerName))
        {
            ctx.PendingMissingField = "CustomerName";
            SetState(session, ConversationState.AwaitingOrderMissingFields);
            await ReplyAsync(seller, "Customer ka naam missing — bataiye?", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(pending.Phone))
        {
            ctx.PendingMissingField = "Phone";
            SetState(session, ConversationState.AwaitingOrderMissingFields);
            await ReplyAsync(seller, "Customer ka phone number bhi bataiye?", ct);
            return;
        }

        await ApplyDiscountAsync(seller, pending, ct);

        var duplicate = await FindPossibleDuplicateAsync(seller, pending, ct);
        if (duplicate is not null)
        {
            pending.DuplicateOfOrderId = duplicate.Id;
            SetState(session, ConversationState.AwaitingDuplicateOrderConfirmation);
            await ReplyAsync(seller,
                $"⚠️ {pending.CustomerName} ka isi jaisa order thodi der pehle bhi save hua tha (#{duplicate.Id}).\n\n" +
                "1️⃣ Ye naya/alag order hai — save karein\n2️⃣ Galti se dobara bhej diya — cancel karein", ct);
            return;
        }

        SetState(session, ConversationState.AwaitingOrderConfirmation);
        await ReplyAsync(seller, BuildConfirmationText(pending), ct);
    }

    private async Task ApplyDiscountAsync(Seller seller, PendingOrderData pending, CancellationToken ct)
    {
        pending.Subtotal = pending.Items.Sum(i => i.UnitPrice * i.Quantity);
        pending.DiscountAmount = 0;

        if (!string.IsNullOrWhiteSpace(pending.DiscountCode))
        {
            var discount = await _db.Discounts.FirstOrDefaultAsync(d =>
                d.SellerId == seller.Id && d.IsActive && d.Code == pending.DiscountCode!.ToUpperInvariant() &&
                (d.ExpiresAt == null || d.ExpiresAt > DateTime.UtcNow), ct);

            if (discount is not null)
            {
                pending.DiscountAmount = discount.Type == DiscountType.Percent
                    ? Math.Round(pending.Subtotal * discount.Value / 100m, 2)
                    : discount.Value;
            }
        }

        pending.Total = Math.Max(0, pending.Subtotal - pending.DiscountAmount);
    }

    private Task<Order?> FindPossibleDuplicateAsync(Seller seller, PendingOrderData pending, CancellationToken ct)
    {
        var window = DateTime.UtcNow.AddMinutes(-30);
        var firstItemName = pending.Items.FirstOrDefault()?.ProductName;
        return _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id
                && o.Status != OrderStatus.Cancelled
                && o.CreatedAt >= window
                && o.Customer!.Name.ToLower() == pending.CustomerName!.ToLower()
                && o.Items.Any(i => i.ProductNameSnapshot == firstItemName))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct)!;
    }

    private static string BuildConfirmationText(PendingOrderData pending)
    {
        var itemLines = pending.Items.Count == 1
            ? $"Product: {ItemLabel(pending.Items[0])}"
            : "Products:\n" + string.Join("\n", pending.Items.Select(i => "  " + ItemLabel(i)));

        var lines = new List<string>
        {
            "📝 Confirm order:",
            $"Customer: {pending.CustomerName}",
            itemLines
        };
        if (!string.IsNullOrWhiteSpace(pending.Phone)) lines.Add($"Phone: {pending.Phone}");
        if (!string.IsNullOrWhiteSpace(pending.Address)) lines.Add($"Address: {pending.Address}");
        if (pending.DiscountAmount > 0) lines.Add($"Discount ({pending.DiscountCode}): -{Formatters.Money(pending.DiscountAmount)}");
        lines.Add($"Total: {Formatters.Money(pending.Total)}");
        lines.Add("");
        lines.Add("Reply YES to save, ya EDIT to fix.");
        return string.Join("\n", lines);
    }

    private static string ItemLabel(PendingOrderItemData item) =>
        item.Quantity > 1
            ? $"{item.Quantity}x {item.ProductName} - {Formatters.Money(item.UnitPrice * item.Quantity)}"
            : $"{item.ProductName} - {Formatters.Money(item.UnitPrice)}";

    private static readonly HashSet<string> CancelWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cancel", "stop", "back", "no", "nahi", "nahin", "chhod do", "rehne do", "رہنے دو", "نہیں"
    };

    // While an order is half-entered, a cancel word or any command (menu, help, catalog, ...) abandons the draft
    // instead of being swallowed as a customer name or phone number. Product-add lines keep the draft.
    private async Task<bool> TryLeaveOrderDraftAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var trimmed = message.Trim();
        var command = CommandParser.TryParse(trimmed);
        var isCancel = CancelWords.Contains(trimmed);
        var isCommand = command is not null && command.Kind is not (CommandKind.AddProduct or CommandKind.AddProductsBulk);
        if (!isCancel && !isCommand) return false;

        ctx.PendingOrder = null;
        ctx.PendingMissingField = null;
        ctx.PendingNewProductName = null;
        ctx.QueuedSeparateOrderItems = null;
        ctx.QueuedOrderTemplate = null;
        SetState(session, ConversationState.Idle);

        if (isCancel)
        {
            await ReplyAsync(seller, "Theek hai, order cancel kar diya. Naya order bhejne ke liye tafseel likhein (naam, product, phone, address).", ct);
            return true;
        }

        await ReplyAsync(seller, "↩️ Adhoora order chhod diya.", ct);
        await ExecuteCommandAsync(seller, session, ctx, command!, ct);
        return true;
    }

    private async Task HandleOrderConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (CommandParser.IsAffirmative(message))
        {
            var pending = ctx.PendingOrder!;
            ctx.PendingOrder = null;
            SetState(session, ConversationState.Idle);
            var order = await SaveOrderFromDraftAsync(seller, pending, ct);
            await ReplyAsync(seller, $"✅ Order saved as {Formatters.Status(order.Status)} (#{order.Id}).", ct);
            await AfterOrderSavedAsync(seller, session, ctx, order, ct);
            return;
        }

        if (message.Trim().Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            ctx.PendingOrder = null;
            SetState(session, ConversationState.Idle);
            await ReplyAsync(seller, "Theek hai, order cancel kar diya — dobara sahi tafseel bhej dein.", ct);
            return;
        }

        await ReplyAsync(seller, "Reply YES to save, ya EDIT to fix.", ct);
    }

    private async Task HandleOrderMissingFieldAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var pending = ctx.PendingOrder!;

        switch (ctx.PendingMissingField)
        {
            case "ProductChoice":
                if (message.Trim() == "1")
                {
                    ctx.PendingMissingField = "NewProductPrice";
                    await ReplyAsync(seller, $"{ctx.PendingNewProductName} ki price kya hai?", ct);
                    return;
                }
                if (message.Trim() == "2")
                {
                    ctx.PendingMissingField = "MappedProductName";
                    await ReplyAsync(seller, "Kaunsa existing product hai? (naam batayein)", ct);
                    return;
                }
                await ReplyAsync(seller, "Reply 1 ya 2.", ct);
                return;

            case "NewProductPrice":
                if (!decimal.TryParse(message.Trim(), out var price))
                {
                    await ReplyAsync(seller, "Sirf price number mein bhejein (e.g. 4200).", ct);
                    return;
                }
                _db.Products.Add(new Product { SellerId = seller.Id, Name = ctx.PendingNewProductName!, Price = price });
                ApplyResolvedProductToPendingItem(pending, ctx.PendingNewProductName!, ctx.PendingNewProductName!, price);
                await ReplyAsync(seller, $"✅ {ctx.PendingNewProductName} - {Formatters.Money(price)} catalog mein add ho gaya.", ct);
                ctx.PendingNewProductName = null;
                ctx.PendingMissingField = null;
                await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                return;

            case "MappedProductName":
                var mapped = await _db.Products.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Name.ToLower() == message.Trim().ToLower(), ct);
                if (mapped is null)
                {
                    await ReplyAsync(seller, $"\"{message.Trim()}\" catalog mein nahi mila — dobara naam bhejein, ya \"1\" likh kar naya product add karein.", ct);
                    return;
                }
                ApplyResolvedProductToPendingItem(pending, ctx.PendingNewProductName!, mapped.Name, mapped.Price);
                ctx.PendingNewProductName = null;
                ctx.PendingMissingField = null;
                await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                return;

            case "CustomerName":
                pending.CustomerName = message.Trim();
                ctx.PendingMissingField = null;
                await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                return;

            case "Phone":
                if (message.Count(char.IsDigit) < 10)
                {
                    await ReplyAsync(seller, "Phone number sahi nahi lagta — sirf number bhejein (e.g. 03001234567), ya \"cancel\" likhein.", ct);
                    return;
                }
                pending.Phone = message.Trim();
                ctx.PendingMissingField = null;
                await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                return;

            default:
                SetState(session, ConversationState.Idle);
                ctx.PendingOrder = null;
                await ReplyAsync(seller, "Order draft clear kar diya. Dobara try karein.", ct);
                return;
        }
    }

    private static void ApplyResolvedProductToPendingItem(PendingOrderData pending, string originalName, string resolvedName, decimal price)
    {
        var item = pending.Items.First(i => i.UnitPrice < 0 && i.ProductName == originalName);
        item.ProductName = resolvedName;
        item.UnitPrice = price;
    }

    private async Task HandleOrderGroupingChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var pending = ctx.PendingOrder!;
        var choice = message.Trim();

        if (choice == "2")
        {
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
            return;
        }

        if (choice == "1")
        {
            var first = pending.Items[0];
            var queued = pending.Items.Skip(1).ToList();
            ctx.PendingOrder = new PendingOrderData
            {
                CustomerName = pending.CustomerName,
                Phone = pending.Phone,
                Address = pending.Address,
                PaymentMethodText = pending.PaymentMethodText,
                DiscountCode = pending.DiscountCode,
                Items = { first }
            };
            ctx.QueuedSeparateOrderItems = queued;
            ctx.QueuedOrderTemplate = new PendingOrderData
            {
                CustomerName = pending.CustomerName,
                Phone = pending.Phone,
                Address = pending.Address,
                PaymentMethodText = pending.PaymentMethodText,
                DiscountCode = pending.DiscountCode
            };
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
            return;
        }

        await ReplyAsync(seller, "Reply 1 ya 2.", ct);
    }

    private async Task<Order> SaveOrderFromDraftAsync(Seller seller, PendingOrderData pending, CancellationToken ct)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c =>
            c.SellerId == seller.Id && c.Name.ToLower() == pending.CustomerName!.ToLower(), ct);

        if (customer is null)
        {
            customer = new Customer { SellerId = seller.Id, Name = pending.CustomerName!, Phone = pending.Phone, Address = pending.Address };
            _db.Customers.Add(customer);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(pending.Phone)) customer.Phone = pending.Phone;
            if (!string.IsNullOrWhiteSpace(pending.Address)) customer.Address = pending.Address;
        }

        var paymentMethod = (pending.PaymentMethodText ?? "").ToLowerInvariant().Contains("cod")
            ? OrderPaymentMethod.Cod
            : OrderPaymentMethod.Unspecified;

        var order = new Order
        {
            SellerId = seller.Id,
            Customer = customer,
            Status = OrderStatus.Pending,
            PaymentMethod = paymentMethod,
            DiscountCode = pending.DiscountAmount > 0 ? pending.DiscountCode?.ToUpperInvariant() : null,
            Subtotal = pending.Subtotal,
            DiscountAmount = pending.DiscountAmount,
            Total = pending.Total
        };

        foreach (var item in pending.Items)
        {
            var product = await _db.Products.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Name == item.ProductName, ct);
            order.Items.Add(new OrderItem
            {
                ProductId = product?.Id,
                ProductNameSnapshot = item.ProductName,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity
            });
        }

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id,
            ActionType = ActionType.OrderCreated,
            OrderId = order.Id,
            PayloadJson = "{}"
        });

        return order;
    }

    private async Task AfterOrderSavedAsync(Seller seller, ConversationSession session, SessionContextData ctx, Order order, CancellationToken ct)
    {
        await CheckLoyaltyThresholdAsync(seller, order.CustomerId, ct);

        if (ctx.QueuedSeparateOrderItems is { Count: > 0 })
        {
            var next = ctx.QueuedSeparateOrderItems[0];
            ctx.QueuedSeparateOrderItems.RemoveAt(0);
            var template = ctx.QueuedOrderTemplate!;
            ctx.PendingOrder = new PendingOrderData
            {
                CustomerName = template.CustomerName,
                Phone = template.Phone,
                Address = template.Address,
                PaymentMethodText = template.PaymentMethodText,
                DiscountCode = template.DiscountCode,
                Items = { next }
            };
            if (ctx.QueuedSeparateOrderItems.Count == 0)
            {
                ctx.QueuedSeparateOrderItems = null;
                ctx.QueuedOrderTemplate = null;
            }
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
        }
    }

    private async Task HandleDuplicateOrderConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var choice = message.Trim();
        if (choice == "1")
        {
            var pending = ctx.PendingOrder!;
            ctx.PendingOrder = null;
            SetState(session, ConversationState.Idle);
            var order = await SaveOrderFromDraftAsync(seller, pending, ct);
            await ReplyAsync(seller, $"✅ Order saved as {Formatters.Status(order.Status)} (#{order.Id}).", ct);
            await AfterOrderSavedAsync(seller, session, ctx, order, ct);
            return;
        }

        if (choice == "2")
        {
            ctx.PendingOrder = null;
            SetState(session, ConversationState.Idle);
            await ReplyAsync(seller, "✅ Theek hai, duplicate cancel kar diya. Koi tabdeeli nahi hui.", ct);
            return;
        }

        await ReplyAsync(seller, "Reply 1 ya 2.", ct);
    }

    private async Task HandleClarificationChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var options = ctx.ClarificationOptions ?? DefaultClarificationOptions.ToList();

        if (CommandParser.TryParse(message) is { } command)
        {
            ctx.ClarificationOptions = null;
            SetState(session, ConversationState.Idle);
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
            return;
        }

        if (int.TryParse(message.Trim(), out var idx) && idx >= 1 && idx <= options.Count)
        {
            ctx.ClarificationOptions = null;
            SetState(session, ConversationState.Idle);
            var chosen = options[idx - 1];
            var reply = chosen.Contains("order update", StringComparison.OrdinalIgnoreCase) || chosen.Contains("status", StringComparison.OrdinalIgnoreCase)
                ? "Kaunsa order aur naya status? (e.g. \"mark 3 shipped\")"
                : "Theek — order ki tafseel bhej dein: naam, product, phone, address";
            await ReplyAsync(seller, reply, ct);
            return;
        }

        await ReplyAsync(seller, $"Reply number se (1-{options.Count}), ya \"help\" likhein.", ct);
    }

    private async Task CheckLoyaltyThresholdAsync(Seller seller, int customerId, CancellationToken ct)
    {
        var rule = await _db.LoyaltyRules.FirstOrDefaultAsync(r => r.SellerId == seller.Id && r.IsActive, ct);
        if (rule is null) return;

        var orderCount = await _db.Orders.CountAsync(o => o.CustomerId == customerId && o.Status != OrderStatus.Cancelled, ct);
        if (orderCount > 0 && orderCount % rule.OrderThreshold == 0)
        {
            var customer = await _db.Customers.FindAsync(new object?[] { customerId }, ct);
            await ReplyAsync(seller,
                $"🌟 {customer?.Name} ka {orderCount}th order hai — {rule.DiscountPercent}% loyalty discount apply karna chahenge?", ct);
        }
    }
}
