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

    /// <summary>A sellable catalog listing plus its wholesale tiers; <see cref="Label"/> is what the AI sees and matches against.</summary>
    private sealed record CatalogEntry(Product Product, string Label, IReadOnlyList<PriceTier> Tiers);

    private async Task<List<CatalogEntry>> LoadCatalogAsync(Seller seller, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Id).ToListAsync(ct);
        var ids = products.Select(p => p.Id).ToList();
        var tiers = await _db.PriceTiers.Where(t => ids.Contains(t.ProductId)).ToListAsync(ct);
        return products.Select(p => new CatalogEntry(p, Formatters.ProductLabel(p),
            tiers.Where(t => t.ProductId == p.Id).OrderBy(t => t.MinQty).ToList())).ToList();
    }

    private static AiAnalysisContext AiContext(Seller seller, List<CatalogEntry> catalog) => new()
    {
        BusinessName = seller.BusinessName ?? "",
        PreferredLanguage = seller.PreferredLanguage,
        Catalog = catalog.Select(c => new AiCatalogItem
        {
            Name = c.Tiers.Count > 0 ? $"{c.Label} (sold per {c.Product.UnitType})" : c.Label,
            Price = c.Tiers.Count > 0 ? c.Tiers[0].PricePerUnit : c.Product.Price
        }).ToList()
    };

    private async Task HandleFreeformMessageAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync(seller, ct);
        var analysis = await _ai.AnalyzeMessageAsync(AiContext(seller, catalog), message, ct);
        await HandleAnalysisAsync(seller, session, ctx, analysis, catalog, fromScreenshot: false, ct);
    }

    private async Task HandleAnalysisAsync(Seller seller, ConversationSession session, SessionContextData ctx, AiMessageAnalysis analysis,
        List<CatalogEntry> catalog, bool fromScreenshot, CancellationToken ct)
    {
        if (analysis.Intent == "off_topic")
        {
            var opener = string.IsNullOrWhiteSpace(analysis.ClarificationQuestion) ? "😊" : $"😊 {analysis.ClarificationQuestion!.Trim()}";
            await ReplyAsync(seller, $"{opener} Main sirf orders/sales manage karne mein madad karta hoon.\nJab zaroorat ho, \"menu\" likh dein.", ct);
            return;
        }

        if (analysis.Intent == "customer_feedback" && analysis.Feedback is { } feedback)
        {
            await SaveCustomerFeedbackAsync(seller, feedback, ct);
            return;
        }

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

        var drafts = new[] { analysis.Order }.Concat(analysis.AdditionalOrders)
            .Select(d => BuildPendingOrder(d, catalog, fromScreenshot)).ToList();

        if (drafts.Count > 1)
        {
            foreach (var d in drafts) await ApplyDiscountAsync(seller, d, ct);
            if (drafts.All(IsDraftComplete))
            {
                ctx.MultiOrders = drafts;
                SetState(session, ConversationState.AwaitingMultiOrderConfirmation);
                var lines = drafts.Select((d, i) => $"{i + 1}️⃣ {d.CustomerName} - {string.Join(" + ", d.Items.Select(ShortItem))} - {d.Phone}");
                await ReplyAsync(seller,
                    $"📝 Mujhe {drafts.Count} alag orders mile:\n\n{string.Join("\n", lines)}\n\n" +
                    $"{(drafts.Count == 2 ? "Dono" : "Sab")} confirm karein? Reply YES, ya number bata kar sirf ek confirm karein.", ct);
                return;
            }

            ctx.PendingOrder = drafts[0];
            ctx.QueuedOrders = drafts.Skip(1).ToList();
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
            return;
        }

        if (analysis.IsAmbiguousItemGrouping && analysis.Order.Items.Count >= 2)
        {
            ctx.PendingOrder = drafts[0];
            SetState(session, ConversationState.AwaitingOrderGroupingChoice);
            await ReplyAsync(seller,
                "Thoda confusion hai — 2 alag orders (har item alag) ya ek order mein dono?\n\n1️⃣ 2 separate orders\n2️⃣ 1 order, saare items", ct);
            return;
        }

        ctx.PendingOrder = drafts[0];
        await ContinueResolvingDraftAsync(seller, session, ctx, ct);
    }

    private static bool IsDraftComplete(PendingOrderData d) =>
        !string.IsNullOrWhiteSpace(d.CustomerName) && !string.IsNullOrWhiteSpace(d.Phone) && d.Items.Count > 0 && d.Items.All(i => i.UnitPrice >= 0);

    private static string ShortItem(PendingOrderItemData i) => $"{i.Quantity}x {i.ProductName}";

    private static PendingOrderData BuildPendingOrder(AiOrderDraft draft, List<CatalogEntry> catalog, bool fromScreenshot)
    {
        var pending = new PendingOrderData
        {
            CustomerName = draft.CustomerName,
            Phone = draft.Phone,
            Address = draft.Address,
            PaymentMethodText = draft.PaymentMethod,
            DiscountCode = draft.DiscountCode,
            OrderSource = draft.OrderSource,
            FromScreenshot = fromScreenshot
        };

        foreach (var item in draft.Items)
        {
            var quantity = item.Quantity <= 0 ? 1 : item.Quantity;
            var entry = FindCatalogEntry(catalog, item.MatchedCatalogProductName ?? item.ProductName);
            var line = new PendingOrderItemData { ProductName = entry?.Product.Name ?? item.ProductName, Quantity = quantity, UnitPrice = -1m };
            if (entry is not null) ApplyCatalogEntry(line, entry);
            pending.Items.Add(line);
        }

        return pending;
    }

    private static CatalogEntry? FindCatalogEntry(List<CatalogEntry> catalog, string name)
    {
        var wanted = name.Trim();
        if (wanted.EndsWith(')') && wanted.Contains(" (sold per ")) wanted = wanted[..wanted.IndexOf(" (sold per ", StringComparison.Ordinal)];
        return catalog.FirstOrDefault(c => string.Equals(c.Label, wanted, StringComparison.OrdinalIgnoreCase))
               ?? catalog.FirstOrDefault(c => string.Equals(c.Product.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves price: the matching wholesale tier for the quantity, else the listing price.</summary>
    private static void ApplyCatalogEntry(PendingOrderItemData line, CatalogEntry entry)
    {
        line.ProductId = entry.Product.Id;
        line.ProductName = entry.Product.Name;
        line.UnitType = entry.Product.UnitType;
        line.UnitQty = entry.Product.UnitQty;
        var tier = entry.Tiers.Where(t => t.MinQty <= line.Quantity).MaxBy(t => t.MinQty) ?? entry.Tiers.FirstOrDefault();
        line.IsTierPrice = tier is not null;
        line.TierMinQty = tier?.MinQty;
        line.UnitPrice = tier?.PricePerUnit ?? entry.Product.Price;
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
            var minutes = Math.Max(1, (int)(DateTime.UtcNow - duplicate.CreatedAt).TotalMinutes);
            await ReplyAsync(seller,
                $"⚠️ {pending.CustomerName} ka isi jaisa order {minutes} min pehle bhi save hua tha (#{duplicate.Id}).\n\n" +
                "1️⃣ Ye naya/alag order hai — save karein\n2️⃣ Galti se dobara bhej diya — cancel karein", ct);
            return;
        }

        SetState(session, ConversationState.AwaitingOrderConfirmation);
        await ReplyAsync(seller, BuildConfirmationText(seller.PreferredLanguage, pending), ct);
    }

    private async Task ApplyDiscountAsync(Seller seller, PendingOrderData pending, CancellationToken ct)
    {
        pending.Subtotal = pending.Items.Where(i => i.UnitPrice >= 0).Sum(i => i.UnitPrice * i.Quantity);
        pending.DiscountAmount = 0;

        if (!string.IsNullOrWhiteSpace(pending.DiscountCode))
        {
            var code = pending.DiscountCode.ToUpperInvariant();
            var now = DateTime.UtcNow;
            var discount = await _db.Discounts.FirstOrDefaultAsync(d =>
                d.SellerId == seller.Id && d.IsActive && d.Code == code && (d.ExpiresAt == null || d.ExpiresAt > now), ct);

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

    private static OrderPaymentMethod ResolvePaymentMethod(string? text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (t.Contains("safepay") || t.Contains("card") || t.Contains("gateway")) return OrderPaymentMethod.Gateway;
        if (t.Contains("jazz") || t.Contains("easy") || t.Contains("bank") || t.Contains("advance") || t.Contains("prepaid")) return OrderPaymentMethod.Manual;
        return OrderPaymentMethod.Cod; // COD is the default payment method (spec V1)
    }

    private static string PaymentLabel(OrderPaymentMethod method, string? text) => method switch
    {
        OrderPaymentMethod.Cod => "COD (delivery par cash)",
        OrderPaymentMethod.Gateway => "Online (card/wallet link)",
        _ => string.IsNullOrWhiteSpace(text) ? "Advance (JazzCash/Easypaisa/Bank)" : text!
    };

    private static string BuildConfirmationText(string language, PendingOrderData pending)
    {
        var lang = Lang.Normalize(language);
        if (lang == Lang.UrduScript)
        {
            var urdu = new List<string>
            {
                "📝 آرڈر کی تصدیق کریں:",
                $"گاہک: {pending.CustomerName}",
                "پروڈکٹ: " + string.Join("، ", pending.Items.Select(i => $"{i.ProductName} — {i.Quantity} عدد")),
                $"قیمت: {pending.Total:#,0} روپے"
            };
            if (!string.IsNullOrWhiteSpace(pending.Phone)) urdu.Add($"فون: {pending.Phone}");
            if (!string.IsNullOrWhiteSpace(pending.Address)) urdu.Add($"پتہ: {pending.Address}");
            urdu.Add("");
            urdu.Add("تصدیق کے لیے \"ہاں\" لکھیں");
            return string.Join("\n", urdu);
        }

        var lines = new List<string>
        {
            pending.FromScreenshot ? "📝 Screenshot se order parh liya:" : "📝 Confirm order:",
            $"Customer: {pending.CustomerName}"
        };

        if (pending.Items.Count == 1 && pending.Items[0] is { IsTierPrice: true } tier)
        {
            lines.Add($"Product: {tier.ProductName} - {Formatters.Quantity(tier.Quantity)}{Formatters.UnitShort(tier.UnitType)}");
            lines.Add($"Rate: {Formatters.Money(tier.UnitPrice)}/{Formatters.UnitShort(tier.UnitType).Trim()} ({Formatters.Quantity(tier.TierMinQty ?? 1)}{Formatters.UnitShort(tier.UnitType)}+ tier)");
        }
        else if (pending.Items.Count == 1 && IsPack(pending.Items[0]))
        {
            var item = pending.Items[0];
            lines.Add($"Product: {item.ProductName} ({Formatters.Quantity(item.UnitQty)}{Formatters.UnitShort(item.UnitType)} pack) x{item.Quantity} = " +
                      $"{Formatters.Quantity(item.UnitQty * item.Quantity)}{Formatters.UnitShort(item.UnitType)} total");
            lines.Add($"Price: {Formatters.Money(item.UnitPrice * item.Quantity)}");
        }
        else
        {
            lines.Add(pending.Items.Count == 1
                ? $"Product: {ItemLabel(pending.Items[0])}"
                : "Products:\n" + string.Join("\n", pending.Items.Select(i => "  " + ItemLabel(i))));
        }

        if (!string.IsNullOrWhiteSpace(pending.Phone)) lines.Add($"Phone: {pending.Phone}");
        if (!string.IsNullOrWhiteSpace(pending.Address)) lines.Add($"Address: {pending.Address}");
        lines.Add($"Payment: {PaymentLabel(ResolvePaymentMethod(pending.PaymentMethodText), pending.PaymentMethodText)}");
        if (!string.IsNullOrWhiteSpace(pending.OrderSource)) lines.Add($"Source: {Formatters.SourceLabel(pending.OrderSource)}");
        if (pending.DiscountAmount > 0) lines.Add($"Discount ({pending.DiscountCode?.ToUpperInvariant()}): -{Formatters.Money(pending.DiscountAmount)}");
        lines.Add($"Total: {Formatters.Money(pending.Total)}");
        lines.Add("");
        lines.Add("Reply YES to save, ya EDIT to fix.");
        return string.Join("\n", lines);
    }

    private static bool IsPack(PendingOrderItemData item) => item.UnitType != "piece" || item.UnitQty != 1;

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
        var isCommand = command is not null && command.Kind is not (CommandKind.AddProduct or CommandKind.AddProductsBulk or CommandKind.MoreCustomers);
        if (!isCancel && !isCommand) return false;

        ResetFlowContext(ctx);
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
            await ReplyAsync(seller, SavedText(seller, order), ct);
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

    private static string SavedText(Seller seller, Order order) =>
        Lang.Normalize(seller.PreferredLanguage) == Lang.UrduScript
            ? $"✅ آرڈر محفوظ ہو گیا (#{order.Id})"
            : order.PaymentMethod == OrderPaymentMethod.Cod
                ? $"✅ Order saved as {Formatters.Status(order.Status)} (#{order.Id}) — COD."
                : $"✅ Order saved as {Formatters.Status(order.Status)} (#{order.Id}).";

    private async Task HandleMultiOrderConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var drafts = ctx.MultiOrders ?? new List<PendingOrderData>();
        List<PendingOrderData> chosen;
        if (CommandParser.IsAffirmative(message)) chosen = drafts;
        else if (int.TryParse(message.Trim().TrimEnd('.'), out var n) && n >= 1 && n <= drafts.Count) chosen = new() { drafts[n - 1] };
        else
        {
            await ReplyAsync(seller, $"Reply YES (sab save), ya number (1-{drafts.Count}) sirf ek ke liye, ya \"cancel\".", ct);
            return;
        }

        ctx.MultiOrders = null;
        SetState(session, ConversationState.Idle);
        var saved = new List<Order>();
        foreach (var draft in chosen) saved.Add(await SaveOrderFromDraftAsync(seller, draft, ct));
        await ReplyAsync(seller, saved.Count == 1
            ? $"✅ Order saved (#{saved[0].Id})."
            : $"✅ {saved.Count} orders saved ({string.Join(", ", saved.Select(o => $"#{o.Id}"))}).", ct);
        foreach (var order in saved) await CheckLoyaltyThresholdAsync(seller, session, ctx, order, ct);
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
                if (!decimal.TryParse(message.Trim().Replace("Rs.", "", StringComparison.OrdinalIgnoreCase).Replace(",", ""), out var price))
                {
                    await ReplyAsync(seller, "Sirf price number mein bhejein (e.g. 4200).", ct);
                    return;
                }
                var product = new Product { SellerId = seller.Id, Name = ctx.PendingNewProductName!, Price = price };
                _db.Products.Add(product);
                await _db.SaveChangesAsync(ct);
                ApplyResolvedProductToPendingItem(pending, ctx.PendingNewProductName!, new CatalogEntry(product, product.Name, Array.Empty<PriceTier>()));
                await ReplyAsync(seller, $"✅ {ctx.PendingNewProductName} - {Formatters.Money(price)} catalog mein add ho gaya.", ct);
                ctx.PendingNewProductName = null;
                ctx.PendingMissingField = null;
                await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                return;

            case "MappedProductName":
                var catalog = await LoadCatalogAsync(seller, ct);
                var mapped = FindCatalogEntry(catalog, message.Trim());
                if (mapped is null)
                {
                    await ReplyAsync(seller, $"\"{message.Trim()}\" catalog mein nahi mila — dobara naam bhejein, ya \"1\" likh kar naya product add karein.", ct);
                    return;
                }
                ApplyResolvedProductToPendingItem(pending, ctx.PendingNewProductName!, mapped);
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

    private static void ApplyResolvedProductToPendingItem(PendingOrderData pending, string originalName, CatalogEntry entry)
    {
        var item = pending.Items.First(i => i.UnitPrice < 0 && i.ProductName == originalName);
        ApplyCatalogEntry(item, entry);
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
            ctx.PendingOrder = CopyHeader(pending);
            ctx.PendingOrder.Items.Add(first);
            ctx.QueuedSeparateOrderItems = queued;
            ctx.QueuedOrderTemplate = CopyHeader(pending);
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
            return;
        }

        await ReplyAsync(seller, "Reply 1 ya 2.", ct);
    }

    private static PendingOrderData CopyHeader(PendingOrderData source) => new()
    {
        CustomerName = source.CustomerName,
        Phone = source.Phone,
        Address = source.Address,
        PaymentMethodText = source.PaymentMethodText,
        DiscountCode = source.DiscountCode,
        OrderSource = source.OrderSource,
        FromScreenshot = source.FromScreenshot
    };

    private async Task<Order> SaveOrderFromDraftAsync(Seller seller, PendingOrderData pending, CancellationToken ct)
    {
        var lowerName = pending.CustomerName!.ToLower();
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.SellerId == seller.Id && c.Name.ToLower() == lowerName, ct);

        if (customer is null)
        {
            customer = new Customer { SellerId = seller.Id, Name = pending.CustomerName!, Phone = pending.Phone, Address = pending.Address };
            _db.Customers.Add(customer);
        }
        else
        {
            customer.DeletedAt = null; // a new order brings a removed customer back
            if (!string.IsNullOrWhiteSpace(pending.Phone)) customer.Phone = pending.Phone;
            if (!string.IsNullOrWhiteSpace(pending.Address)) customer.Address = pending.Address;
        }

        var order = new Order
        {
            SellerId = seller.Id,
            Customer = customer,
            Status = OrderStatus.Pending,
            PaymentMethod = ResolvePaymentMethod(pending.PaymentMethodText),
            DiscountCode = pending.DiscountAmount > 0 ? pending.DiscountCode?.ToUpperInvariant() : null,
            OrderSource = pending.OrderSource?.ToLowerInvariant(),
            Subtotal = pending.Subtotal,
            DiscountAmount = pending.DiscountAmount,
            Total = pending.Total
        };

        foreach (var item in pending.Items)
        {
            var productId = item.ProductId
                ?? (await _db.Products.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Name == item.ProductName, ct))?.Id;
            order.Items.Add(new OrderItem
            {
                ProductId = productId,
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
        if (ctx.QueuedSeparateOrderItems is { Count: > 0 })
        {
            var next = ctx.QueuedSeparateOrderItems[0];
            ctx.QueuedSeparateOrderItems.RemoveAt(0);
            ctx.PendingOrder = CopyHeader(ctx.QueuedOrderTemplate!);
            ctx.PendingOrder.Items.Add(next);
            if (ctx.QueuedSeparateOrderItems.Count == 0)
            {
                ctx.QueuedSeparateOrderItems = null;
                ctx.QueuedOrderTemplate = null;
            }
            await CheckLoyaltyThresholdAsync(seller, null, ctx, order, ct);
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
            return;
        }

        if (ctx.QueuedOrders is { Count: > 0 })
        {
            ctx.PendingOrder = ctx.QueuedOrders[0];
            ctx.QueuedOrders.RemoveAt(0);
            if (ctx.QueuedOrders.Count == 0) ctx.QueuedOrders = null;
            await CheckLoyaltyThresholdAsync(seller, null, ctx, order, ct);
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
            return;
        }

        await CheckLoyaltyThresholdAsync(seller, session, ctx, order, ct);
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
            await ReplyAsync(seller, SavedText(seller, order), ct);
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

    /// <summary>
    /// Screen 26: when a new order is the customer's Nth (loyalty threshold), offer the loyalty discount on it.
    /// With no session (mid-queue / form submission) it only informs.
    /// </summary>
    private async Task CheckLoyaltyThresholdAsync(Seller seller, ConversationSession? session, SessionContextData ctx, Order order, CancellationToken ct)
    {
        var rule = await _db.LoyaltyRules.FirstOrDefaultAsync(r => r.SellerId == seller.Id && r.IsActive, ct);
        if (rule is null || rule.OrderThreshold <= 0) return;

        var orderCount = await _db.Orders.CountAsync(o => o.CustomerId == order.CustomerId && o.Status != OrderStatus.Cancelled, ct);
        if (orderCount == 0 || orderCount % rule.OrderThreshold != 0) return;

        var customer = await _db.Customers.FindAsync(new object?[] { order.CustomerId }, ct);
        var text = $"🌟 {customer?.Name} ka {Formatters.Ordinal(orderCount)} order hai — {rule.DiscountPercent:0.##}% loyalty discount apply karna chahenge?";
        if (session is null)
        {
            await ReplyAsync(seller, $"{text}\n(Baad mein discount code se de saktay hain.)", ct);
            return;
        }

        ctx.LoyaltyOrderId = order.Id;
        ctx.LoyaltyDiscountPercent = rule.DiscountPercent;
        SetState(session, ConversationState.AwaitingLoyaltyDiscountConfirmation);
        await ReplyAsync(seller, $"{text}\nReply YES ya NO.", ct);
    }

    private async Task HandleLoyaltyDiscountConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var orderId = ctx.LoyaltyOrderId;
        var percent = ctx.LoyaltyDiscountPercent ?? 0;
        ctx.LoyaltyOrderId = null;
        ctx.LoyaltyDiscountPercent = null;
        SetState(session, ConversationState.Idle);

        if (!CommandParser.IsAffirmative(message))
        {
            if (CommandParser.TryParse(message) is { } command)
            {
                await ExecuteCommandAsync(seller, session, ctx, command, ct);
                return;
            }
            await ReplyAsync(seller, "Theek hai, loyalty discount apply nahi kiya.", ct);
            return;
        }

        var order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.Id == orderId && o.SellerId == seller.Id, ct);
        if (order is null) return;

        var off = Math.Round(order.Total * percent / 100m, 0);
        order.DiscountAmount += off;
        order.Total = Math.Max(0, order.Total - off);
        order.DiscountCode ??= "LOYALTY";
        await ReplyAsync(seller, $"✅ Loyalty discount applied — {order.Customer?.Name} ko {Formatters.Money(off)} off mila. Naya total: {Formatters.Money(order.Total)}.", ct);
    }

    /// <summary>Screen 10d: the merchant relays how a buyer felt ("ayesha bahut khush thi order se").</summary>
    private async Task SaveCustomerFeedbackAsync(Seller seller, AiCustomerFeedback feedback, CancellationToken ct)
    {
        var lower = feedback.CustomerName.ToLower();
        var order = await _db.Orders.Include(o => o.Customer)
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Customer!.Name.ToLower() == lower)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);

        _db.CustomerFeedbacks.Add(new CustomerFeedback
        {
            SellerId = seller.Id,
            OrderId = order?.Id,
            CustomerName = order?.Customer?.Name ?? feedback.CustomerName,
            Text = feedback.Text,
            Sentiment = feedback.Sentiment?.ToLowerInvariant()
        });

        await ReplyAsync(seller, order is null
            ? $"✅ Noted — {feedback.CustomerName} ka feedback save ho gaya."
            : $"✅ Noted — feedback saved against Order #{order.Id} ({order.Customer?.Name}).", ct);
    }
}
