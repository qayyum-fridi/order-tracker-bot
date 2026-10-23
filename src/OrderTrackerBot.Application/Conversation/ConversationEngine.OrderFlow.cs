using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Templates;

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

    internal static readonly string NewProductButtonTitle = MessageTemplates.Get("order-flow.048");
    private const int ButtonTitleMax = 20;

    /// <summary>One tap per near-miss product, titled "Name Rs.1800" (cut to the 20 characters a button holds; the same text is what comes back on a tap).</summary>
    private static List<ProductSuggestion> BuildSuggestions(IReadOnlyList<CatalogEntry> catalog, IReadOnlyList<string> nearestLabels)
    {
        var result = new List<ProductSuggestion>();
        foreach (var label in nearestLabels)
        {
            var entry = catalog.FirstOrDefault(c => c.Label == label);
            if (entry is null) continue;
            var price = " Rs." + entry.Product.Price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            var room = ButtonTitleMax - price.Length;
            if (room < 4) continue;
            var name = label.Length <= room ? label : label[..(room - 1)] + "…";
            var title = name + price;
            if (result.Any(r => r.Title == title)) continue;
            result.Add(new ProductSuggestion { Title = title, Label = label });
        }
        return result;
    }

    private async Task<List<CatalogEntry>> LoadCatalogAsync(Seller seller, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Id).ToListAsync(ct);
        var ids = products.Select(p => p.Id).ToList();
        var tiers = await _db.PriceTiers.Where(t => ids.Contains(t.ProductId)).ToListAsync(ct);
        return products.Select(p => new CatalogEntry(p, Formatters.ProductMatchLabel(p),
            tiers.Where(t => t.ProductId == p.Id).OrderBy(t => t.MinQty).ToList())).ToList();
    }

    private static AiAnalysisContext AiContext(Seller seller, List<CatalogEntry> catalog, SessionContextData? ctx = null) => new()
    {
        BusinessName = seller.BusinessName ?? "",
        PreferredLanguage = seller.PreferredLanguage,
        // Names the seller has since priced (typed "Name - price") are no longer waiting.
        PendingPriceProducts = ctx?.PendingPriceProducts?
            .Where(n => !catalog.Any(c => string.Equals(c.Product.Name, n, StringComparison.OrdinalIgnoreCase))).ToList()
            ?? new List<string>(),
        Catalog = catalog.Select(c => new AiCatalogItem
        {
            Name = c.Tiers.Count > 0 ? $"{c.Label} (sold per {c.Product.UnitType})" : c.Label,
            Price = c.Tiers.Count > 0 ? c.Tiers[0].PricePerUnit : c.Product.Price
        }).ToList()
    };

    private async Task HandleFreeformMessageAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync(seller, ct);
        var analysis = await _ai.AnalyzeMessageAsync(AiContext(seller, catalog, ctx), message, ct);
        // "Lawn Suit bech diya Sara ko" is a sale already made, not a new order: say how to record it, instead of a guess.
        if (analysis.Intent == "unclear" && SaleVerbWords.IsMatch(message))
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.030"), ct);
            return;
        }
        // The seller's own words beat the model: "delivery 300" in a single order is that order's delivery charge.
        if (analysis.Order is { } single && analysis.AdditionalOrders.Count == 0 && CommandParser.TryFindDeliveryInOrderText(message, out var delivery))
            single.DeliveryCharge = delivery;
        if (analysis.Order is { } one && analysis.AdditionalOrders.Count == 0 && CommandParser.TryFindAdvanceInOrderText(message, out var advance))
            one.AdvancePaid = advance;
        await HandleAnalysisAsync(seller, session, ctx, analysis, catalog, fromScreenshot: false, ct, message);
    }

    private static readonly System.Text.RegularExpressions.Regex SaleVerbWords = new(
        @"\b(?:bech\w*|bika|bikay\w*|bik\s+gaya|sold|becha)\b|بیچ\w*|بکا|بکے|بیچا",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex DiscountWords = new(
        @"discount|coupon|promo|voucher|offer|code|sale|رعایت|ڈسکاؤنٹ|ڈسکاونٹ|کوپن|آفر|کوڈ|چھوٹ",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex CategoryWords = new(
        @"categor|qism|kism|کیٹیگری|کیٹگری|کیٹیگریز|قسم|زمرہ",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private async Task HandleAnalysisAsync(Seller seller, ConversationSession session, SessionContextData ctx, AiMessageAnalysis analysis,
        List<CatalogEntry> catalog, bool fromScreenshot, CancellationToken ct, string? sourceText = null)
    {
        // A word that names a bot feature (discount, stock, expense...) is never small talk: ask which command was meant.
        if (analysis.Intent is "off_topic" or "unclear" && analysis.Order is null && analysis.ClarificationOptions.Count == 0
            && TryKeywordClarification(sourceText, out var keywordQuestion, out var keywordOptions))
        {
            await AskClarificationAsync(seller, session, ctx, keywordQuestion, keywordOptions, ct);
            return;
        }

        if (analysis.Intent == "off_topic")
        {
            var opener = string.IsNullOrWhiteSpace(analysis.ClarificationQuestion) ? "😊" : $"😊 {analysis.ClarificationQuestion!.Trim()}";
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.001", opener), ct);
            return;
        }

        if (analysis.Intent == "create_discount")
        {
            // The model picks this for anything that starts "create ..." ("create category: Women Cloths"). Only a message that really talks
            // about a discount may open the discount flow, which would otherwise swallow the seller's next messages as a discount code.
            if (!string.IsNullOrWhiteSpace(sourceText) && !DiscountWords.IsMatch(sourceText))
            {
                await ReplyAsync(seller, CategoryWords.IsMatch(sourceText)
                    ? MessageTemplates.Get("order-flow.032")
                    : MessageTemplates.Get("order-flow.033"), ct);
                return;
            }

            SetState(session, ConversationState.AwaitingDiscountDetails);
            await ReplyAsync(seller, HowToText("discount"), ct);
            return;
        }

        if (analysis.Intent == "customer_feedback" && analysis.Feedback is { } feedback)
        {
            await SaveCustomerFeedbackAsync(seller, feedback, ct);
            return;
        }

        if (analysis.Intent == "support_query" && analysis.SupportQuery is { } supportQuery)
        {
            await HandleForwardedQueryAsync(seller, session, ctx, supportQuery.CustomerName, supportQuery.Question, ct);
            return;
        }

        if (analysis.Intent == "add_products" && analysis.NewProducts.Count > 0)
        {
            await HandleNewProductsAsync(seller, ctx, analysis.NewProducts, sourceText, ct);
            return;
        }

        if (!analysis.IsOrderAttempt || analysis.Order is null)
        {
            var options = analysis.ClarificationOptions.Count > 0 ? analysis.ClarificationOptions : DefaultClarificationOptions.ToList();
            var question = analysis.ClarificationQuestion ?? "Maaf kijiye, mujhe aapki baat samajh nahi aayi 🙏 Kya aap yeh kehna chahte hain:";
            if (analysis.ClarificationOptions.Count == 0 && TryKeywordClarification(sourceText, out var kwQuestion, out var kwOptions))
                (question, options) = (kwQuestion, kwOptions);
            LogNotUnderstood("ai-clarify", session.State, sourceText);
            ctx.ClarifiedText = sourceText;
            await AskClarificationAsync(seller, session, ctx, question, options, ct);
            return;
        }

        var drafts = new[] { analysis.Order }.Concat(analysis.AdditionalOrders)
            .Select(d => BuildPendingOrder(d, catalog, fromScreenshot)).ToList();

        // "2 pack Lawn Suit" for a product sold per piece: say so instead of saving a quantity in a unit the product is not sold in (nothing is converted).
        if (sourceText is not null && UnitMismatchIn(sourceText, drafts) is var (mismatchName, ownUnit, saidUnit))
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.unitmismatch", mismatchName, ownUnit, saidUnit), ct);
            return;
        }

        // "…, number abhi nahi hai / phone baad mein": a single order is saved without a phone instead of asking for one.
        if (drafts.Count == 1 && string.IsNullOrWhiteSpace(drafts[0].Phone) && sourceText is not null && PhoneNotYetInOrder.IsMatch(sourceText)) drafts[0].PhoneLater = true;

        // Screen 5d-7: after "lead 1 converted", the next single order is the one that lead turned into.
        if (ctx.ConvertingLeadId is { } convertingLeadId && drafts.Count == 1)
        {
            ctx.ConvertingLeadId = null;
            if (await _db.CommentLeads.FirstOrDefaultAsync(l => l.Id == convertingLeadId && l.SellerId == seller.Id, ct) is { } lead)
            {
                drafts[0].CommentLeadId = lead.Id;
                drafts[0].CommentLeadNumber = lead.Number;
                drafts[0].OrderSource ??= "instagram";
            }
        }

        if (drafts.Count > 1)
        {
            foreach (var d in drafts) await ApplyDiscountAsync(seller, d, ct);
            if (drafts.All(IsDraftComplete))
            {
                ctx.MultiOrders = drafts;
                SetState(session, ConversationState.AwaitingMultiOrderConfirmation);
                var lines = drafts.Select((d, i) => $"{i + 1}️⃣ {d.CustomerName} - {string.Join(" + ", d.Items.Select(ShortItem))} - {d.Phone}");
                await ReplyAsync(seller,
                    MessageTemplates.Get("order-flow.002", drafts.Count, string.Join("\n", lines), (drafts.Count == 2 ? "Dono" : "Sab")), ct);
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
                MessageTemplates.Get("order-flow.003"), ct);
            return;
        }

        ctx.PendingOrder = drafts[0];
        await ContinueResolvingDraftAsync(seller, session, ctx, ct);
    }

    private static bool IsDraftComplete(PendingOrderData d) =>
        !string.IsNullOrWhiteSpace(d.CustomerName) && (!string.IsNullOrWhiteSpace(d.Phone) || d.PhoneLater) && d.Items.Count > 0 && d.Items.All(i => i.UnitPrice >= 0);

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
            DeliveryCharge = draft.DeliveryCharge,
            AdvancePaid = draft.AdvancePaid,
            FromScreenshot = fromScreenshot
        };

        foreach (var item in draft.Items)
        {
            var quantity = item.Quantity <= 0 ? 1 : item.Quantity;
            var entry = FindCatalogEntry(catalog, item.MatchedCatalogProductName ?? item.ProductName);
            var line = new PendingOrderItemData { ProductName = entry?.Product.Name ?? item.ProductName, Quantity = quantity, UnitPrice = -1m };
            if (entry is not null)
            {
                ApplyCatalogEntry(line, entry);
                // Only a plain per-piece listing is compared: a stated price for a kg / pack listing may mean the pack, the kg or the total.
                if (item.StatedUnitPrice is { } stated && !fromScreenshot && line.UnitType == "piece" && line.UnitQty == 1)
                {
                    // ApplyBuyerPriceGroupAsync compares it again with the buyer's price once the buyer is known.
                    line.WrittenPrice = stated;
                    if (Math.Abs(stated - line.UnitPrice) >= 0.01m) line.StatedPrice = stated;
                }
            }
            pending.Items.Add(line);
        }

        return pending;
    }

    private static CatalogEntry? FindCatalogEntry(List<CatalogEntry> catalog, string name)
    {
        var wanted = name.Trim();
        if (wanted.EndsWith(')') && wanted.Contains(" (sold per ")) wanted = wanted[..wanted.IndexOf(" (sold per ", StringComparison.Ordinal)];
        var byName = catalog.FirstOrDefault(c => string.Equals(c.Label, wanted, StringComparison.OrdinalIgnoreCase))
               ?? catalog.FirstOrDefault(c => string.Equals(c.Product.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        // A design code or SKU typed in the order ("1203 x2", "KRT-001") names its product when exactly one product has it.
        var lower = wanted.ToLowerInvariant();
        var coded = catalog.Where(c => HasProductCode(c.Product, lower)).ToList();
        return coded.Count == 1 ? coded[0] : null;
    }

    /// <summary>
    /// Sets each catalog line's price from the buyer's price group (Product.PriceA/B/C) once the buyer is known. A wholesale tier, a line the seller
    /// already answered and a line with no catalog product keep their price. A price the seller typed is compared with the buyer's price.
    /// </summary>
    private async Task ApplyBuyerPriceGroupAsync(Seller seller, PendingOrderData pending, CancellationToken ct)
    {
        var lines = pending.Items.Where(i => i.ProductId is not null && !i.IsTierPrice && !i.PriceDecided).ToList();
        if (lines.Count == 0) return;

        var buyer = pending.CustomerName?.Trim().ToLower();
        var group = string.IsNullOrEmpty(buyer) ? null
            : (await _db.Customers.FirstOrDefaultAsync(c => c.SellerId == seller.Id && c.Name.ToLower() == buyer, ct))?.PriceGroup;
        var ids = lines.Select(l => l.ProductId!.Value).ToList();
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && ids.Contains(p.Id)).ToListAsync(ct);

        foreach (var line in lines)
        {
            if (products.FirstOrDefault(p => p.Id == line.ProductId) is not { } product) continue;
            var reference = product.PriceFor(group);
            line.UnitPrice = reference;
            line.StatedPrice = line.WrittenPrice is { } written && written != reference ? written : null;
        }
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
        await ApplyBuyerPriceGroupAsync(seller, pending, ct);

        var unresolved = pending.Items.FirstOrDefault(i => i.UnitPrice < 0);
        if (unresolved is not null)
        {
            ctx.PendingNewProductName = unresolved.ProductName;
            ctx.PendingMissingField = "ProductChoice";
            SetState(session, ConversationState.AwaitingOrderMissingFields);
            // A near miss of a catalog name is most likely a typo: offer the nearest products as taps (or name the closest one in the typed hint).
            var catalog = await LoadCatalogAsync(seller, ct);
            IReadOnlyList<string> nearest = TextDistance.Nearest(unresolved.ProductName, catalog.Select(c => c.Label), 2, 3);
            // No near miss: products sharing a word with the name are offered ("Ayesha 2 suit" -> the catalog's "Lawn Suit").
            if (nearest.Count == 0) nearest = WordSharingLabels(unresolved.ProductName, catalog);
            var suggestions = _features.SuggestionButtons ? BuildSuggestions(catalog, nearest) : new List<ProductSuggestion>();
            ctx.ProductSuggestions = suggestions.Count > 0 ? suggestions : null;
            var intro = MessageTemplates.Get("order-flow.049", unresolved.ProductName);
            if (suggestions.Count > 0)
            {
                var titles = suggestions.Select(s => s.Title).ToList();
                if (titles.Count < 3) titles.Add(NewProductButtonTitle);
                await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("order-flow.004", intro), titles, ct);
                return;
            }
            var guess = nearest.Count > 0 ? nearest[0] : null;
            await ReplyAsync(seller, intro + (guess is null ? "" : $"\n\n💡 Kya aap \"{guess}\" keh rahe hain? Haan to likhein: 2 {guess}"), ct);
            return;
        }

        if (pending.Items.FirstOrDefault(i => i.StatedPrice is not null) is { } priced)
        {
            ctx.PendingMissingField = "PriceChoice";
            SetState(session, ConversationState.AwaitingOrderMissingFields);
            await SendChoicesAsync(seller.WhatsAppPhoneNumber,
                MessageTemplates.Get("order-flow.005", priced.ProductName, Formatters.Money(priced.UnitPrice), Formatters.Money(priced.StatedPrice!.Value)),
                new List<ChoiceOption>
                {
                    new($"{Formatters.Money(priced.StatedPrice.Value)} par bechein", "1"),
                    new($"Catalog rate {Formatters.Money(priced.UnitPrice)}", "2"),
                    new("Catalog update karein", "3")
                }, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(pending.CustomerName))
        {
            ctx.PendingMissingField = "CustomerName";
            SetState(session, ConversationState.AwaitingOrderMissingFields);
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.006"), ct);
            return;
        }

        // A phone read from the message ("030011122": a digit short) is checked like one typed as an answer, instead of being saved as it is.
        string? badPhone = null;
        if (!pending.PaidInFull && !string.IsNullOrWhiteSpace(pending.Phone) && !IsPlausiblePhone(pending.Phone))
        {
            badPhone = pending.Phone.Trim();
            pending.Phone = null;
        }

        if (string.IsNullOrWhiteSpace(pending.Phone) && !pending.PaidInFull && !pending.PhoneLater)
        {
            ctx.PendingMissingField = "Phone";
            SetState(session, ConversationState.AwaitingOrderMissingFields);
            var waiting = ctx.QueuedOrders?.Count ?? 0;
            await ReplyAsync(seller, badPhone is null
                ? (waiting > 0 && !string.IsNullOrWhiteSpace(pending.CustomerName)
                    ? MessageTemplates.Get("order-flow.phonemany", pending.CustomerName, waiting)
                    : MessageTemplates.Get("order-flow.034"))
                : MessageTemplates.Get("order-flow.035", badPhone, badPhone.Count(char.IsDigit)), ct);
            return;
        }

        // "Ali" with the phone of the saved "Muhammad Ali": ask before a second, split customer record is created.
        if (!pending.PaidInFull && !pending.CustomerMatchResolved && await FindRelatedCustomerByPhoneAsync(seller, pending, ct) is { } related)
        {
            ctx.PendingMissingField = "CustomerMatch";
            ctx.PendingCustomerMatchName = related.Name;
            SetState(session, ConversationState.AwaitingOrderMissingFields);
            await ReplyAsync(seller,
                MessageTemplates.Get("order-flow.007", related.Name, pending.CustomerName, related.Name, pending.CustomerName), ct);
            return;
        }

        await ApplyDiscountAsync(seller, pending, ct);

        var duplicate = pending.PaidInFull ? null : await FindPossibleDuplicateAsync(seller, pending, ct); // two counter buyers may buy the same thing
        if (duplicate is not null)
        {
            pending.DuplicateOfOrderId = duplicate.Id;
            SetState(session, ConversationState.AwaitingDuplicateOrderConfirmation);
            var minutes = Math.Max(1, (int)(DateTime.UtcNow - duplicate.CreatedAt).TotalMinutes);
            await ReplyAsync(seller,
                MessageTemplates.Get("order-flow.008", pending.CustomerName, minutes, duplicate.Id), ct);
            return;
        }

        SetState(session, ConversationState.AwaitingOrderConfirmation);
        await SendDraftConfirmationAsync(seller, BuildConfirmationText(seller.PreferredLanguage, pending), ct);
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

        pending.DeliveryCharge ??= seller.DefaultDeliveryCharge;
        pending.Total = OrderTotal(pending.Subtotal, pending.DiscountAmount, pending.DeliveryCharge.Value);
    }

    /// <summary>Discounts apply to the goods only; delivery is added on top.</summary>
    private static decimal OrderTotal(decimal subtotal, decimal discount, decimal delivery) => Math.Max(0, subtotal - discount) + delivery;

    private Task<Order?> FindPossibleDuplicateAsync(Seller seller, PendingOrderData pending, CancellationToken ct)
    {
        var window = DateTime.UtcNow.AddMinutes(-30);
        var firstItemName = pending.Items.FirstOrDefault()?.ProductName;
        return _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id
                && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned
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
        OrderPaymentMethod.Cod => MessageTemplates.Get("order-flow.040"),
        OrderPaymentMethod.Gateway => MessageTemplates.Get("order-flow.041"),
        _ => string.IsNullOrWhiteSpace(text) ? "Advance (JazzCash/Easypaisa/Bank)" : text!
    };

    private static string BuildConfirmationText(string language, PendingOrderData pending)
    {
        var lang = Lang.Normalize(language);
        if (lang == Lang.UrduScript)
        {
            var urdu = new List<string>
            {
                MessageTemplates.Get("order-flow.053"),
                $"گاہک: {pending.CustomerName}",
                "پروڈکٹ: " + string.Join("، ", pending.Items.Select(i => $"{i.ProductName} — {i.Quantity} عدد")),
                $"قیمت: {pending.Total:#,0} روپے"
            };
            if (pending.AdvancePaid is > 0) urdu.Add($"ایڈوانس: {pending.AdvancePaid:#,0} روپے");
            if (pending.DeliveryCharge is > 0) urdu.Add($"(ڈیلیوری {pending.DeliveryCharge:#,0} روپے شامل — بدلنے کے لیے لکھیں: delivery 300)");
            if (!string.IsNullOrWhiteSpace(pending.Phone)) urdu.Add($"فون: {pending.Phone}");
            if (!string.IsNullOrWhiteSpace(pending.Address)) urdu.Add($"پتہ: {pending.Address}");
            urdu.Add("");
            urdu.Add("تصدیق کے لیے \"ہاں\" لکھیں");
            return string.Join("\n", urdu);
        }

        var lines = new List<string>
        {
            pending.FromScreenshot ? MessageTemplates.Get("order-flow.054") : MessageTemplates.Get("order-flow.055"),
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
        lines.Add(pending.PaidInFull
            ? $"Payment: {(ResolvePaymentMethod(pending.PaymentMethodText) == OrderPaymentMethod.Cod ? "Cash" : pending.PaymentMethodText)} — counter sale, paid"
              + (pending.SoldAt is { } soldAt ? $" ({soldAt:dd MMM} ki sale)" : "")
            : $"Payment: {PaymentLabel(ResolvePaymentMethod(pending.PaymentMethodText), pending.PaymentMethodText)}");
        if (pending.CommentLeadNumber is { } leadNumber) lines.Add($"Source: Instagram comment lead #{leadNumber}");
        else if (!string.IsNullOrWhiteSpace(pending.OrderSource)) lines.Add($"Source: {Formatters.SourceLabel(pending.OrderSource)}");
        if (pending.DiscountAmount > 0) lines.Add($"Discount ({pending.DiscountCode?.ToUpperInvariant()}): -{Formatters.Money(pending.DiscountAmount)}");
        if (pending.DeliveryCharge is > 0) lines.Add($"Delivery: {Formatters.Money(pending.DeliveryCharge.Value)} (badalne ke liye: \"delivery 300\" ya \"free delivery\")");
        lines.Add($"Total: {Formatters.Money(pending.Total)}");
        if (pending.AdvancePaid is > 0 and var advance)
            lines.Add(advance >= pending.Total ? $"Advance: {Formatters.Money(advance)} — poora paid" : $"Advance: {Formatters.Money(advance)} · Baqi: {Formatters.Money(pending.Total - advance)}");
        lines.Add("");
        lines.Add("Reply YES to save, ya EDIT to fix.");
        return string.Join("\n", lines);
    }

    // Labels double as the typed commands they stand for (a tap arrives as its label; the translating sender maps translated labels back).
    internal static readonly string[] ConfirmButtonLabels = { "✅ Save", "✏️ Edit", "❌ Cancel" };
    private static readonly string[] ConfirmButtonCommands = { "yes", "edit", "cancel" };
    private static readonly string ConfirmTypedHint = MessageTemplates.Get("order-flow.050");
    private const int MaxButtonBodyLength = 1000;

    /// <summary>The order draft with Save / Edit / Cancel taps; plain text with the typed hint when buttons are off or the draft is too long for a button message.</summary>
    private async Task SendDraftConfirmationAsync(Seller seller, string text, CancellationToken ct)
    {
        var body = text.EndsWith(ConfirmTypedHint, StringComparison.Ordinal) ? MessageTemplates.Get("order-flow.051", text[..^ConfirmTypedHint.Length]) : text;
        if (!_features.ConfirmButtons || body.Length > MaxButtonBodyLength)
        {
            await ReplyAsync(seller, text, ct);
            return;
        }
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, body, ConfirmButtonLabels, ct);
    }

    private static bool IsPack(PendingOrderItemData item) => item.UnitType != "piece" || item.UnitQty != 1;

    private static string ItemLabel(PendingOrderItemData item) =>
        item.Quantity > 1
            ? $"{item.Quantity}x {item.ProductName} - {Formatters.Money(item.UnitPrice * item.Quantity)}"
            : $"{item.ProductName} - {Formatters.Money(item.UnitPrice)}";

    // Escape phrases that clear an order draft only when they OPEN the message ("khatam karo", "chhoro yaar", "shuru se karenge").
    // A phrase followed by a negation ("cancel mat karna", "cancel nahi") is not an escape, and a phrase mid-sentence never is.
    private static readonly string[] LeavePhrases =
    {
        "cancel", "back", "chhoro", "chhodo", "chhod do", "chhor do", "rehne do", "khatam karo", "khatam kar do", "shuru se"
    };

    private static readonly string[] NegationWords = { "mat", "nahi", "nahin", "not", "no", "na" };

    private static bool OpensWithLeavePhrase(string text)
    {
        foreach (var phrase in LeavePhrases)
        {
            if (text.Equals(phrase, StringComparison.OrdinalIgnoreCase)) return true;
            if (!text.StartsWith(phrase + " ", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = text[(phrase.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // "Cancel Fashion Boutique": capitalised words after the phrase are a name, not a command.
            if (rest.Any(w => char.IsUpper(w[0]))) return false;
            return !NegationWords.Contains(rest.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase);
        }
        return false;
    }

    // "Nahi bhai, cancel karo": a refusal followed by an instruction to cancel is a cancel. "cancel mat karna" (a negation after the phrase) is not.
    private static readonly Regex NegatedCancelInstruction = new(
        @"^(?:nahi|nahin|no)[\s,.!]*(?:bhai|yar|ji|sir|please|bas)?[\s,.!]*(?:cancel|chhoro|chhodo)\s+(?:karo|kar\s*do|kardo|kar\s+dein|karein|krdo|kr\s+do|kr\s+dein)[.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> CancelWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cancel", "stop", "back", "no", "nahi", "nahin", "chhod do", "chhodo", "chhoro", "chhor do", "rehne do", "رہنے دو", "نہیں"
    };

    /// <summary>Catalog labels that share a word (3+ letters) with a product name the seller typed.</summary>
    private static IReadOnlyList<string> WordSharingLabels(string name, IReadOnlyList<CatalogEntry> catalog)
    {
        var words = NameWords(name);
        if (words.Count == 0) return Array.Empty<string>();
        return catalog.Where(c => NameWords(c.Label).Overlaps(words)).Select(c => c.Label).Take(3).ToList();
    }

    private static HashSet<string> NameWords(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}]+").Where(w => w.Length >= 3).ToHashSet();

    // While an order is half-entered, a cancel word or any command (menu, help, catalog, ...) abandons the draft
    // instead of being swallowed as a customer name or phone number. Product-add lines keep the draft.
    private async Task<bool> TryLeaveOrderDraftAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var trimmed = message.Trim();
        // "delivery 300" while confirming changes this order's delivery, not the seller default.
        if (session.State == ConversationState.AwaitingOrderConfirmation
            && (CommandParser.TryParseDeliveryAmount(trimmed, out _) || trimmed.Length <= 30 && CommandParser.TryFindAdvanceInOrderText(trimmed, out _))) return false;
        var command = CommandParser.TryParse(trimmed);
        var isCancel = CancelWords.Contains(trimmed) || (command is null && (OpensWithLeavePhrase(trimmed) || NegatedCancelInstruction.IsMatch(trimmed)));
        var isCommand = command is not null && command.Kind is not (CommandKind.AddProduct or CommandKind.AddProductsBulk or CommandKind.MoreCustomers);
        if (!isCancel && !isCommand) return false;

        // "edit order 1" for an order that does not exist: say so and keep the draft, instead of throwing it away.
        if (!isCancel && command!.Kind == CommandKind.EditOrder && command.Number is { } missingNo
            && !await _db.Orders.AnyAsync(o => o.SellerId == seller.Id && o.Id == missingNo, ct))
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.028", missingNo), ct);
            return true;
        }

        ResetFlowContext(ctx);
        SetState(session, ConversationState.Idle);

        if (isCancel)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.009"), ct);
            return true;
        }

        await ReplyAsync(seller, MessageTemplates.Get("order-flow.010"), ct);
        await ExecuteCommandAsync(seller, session, ctx, command!, ct);
        return true;
    }

    private async Task HandleOrderConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        // "Reply YES to save": an explicit yes, or "ok" / "theek hai" as agreement to exactly this question.
        if (CommandParser.IsAffirmative(message) || CommandParser.IsAcknowledgement(message))
        {
            var pending = ctx.PendingOrder!;
            var asked = ctx.PendingOrderAskedAt;
            ctx.PendingOrder = null;
            SetState(session, ConversationState.Idle);
            // An old draft is not saved by a late "ok": the seller must see it again and confirm.
            if (!DraftConfirmation.IsFresh(asked, _clock.GetUtcNow().UtcDateTime))
            {
                await ReplyAsync(seller, MessageTemplates.Get("order-flow.011"), ct);
                return;
            }
            var order = await SaveOrderFromDraftAsync(seller, pending, ct);
            await ReplyAsync(seller, SavedText(seller, order, pending), ct);
            await SendOrderActionButtonsAsync(seller, order, ct);
            await AfterOrderSavedAsync(seller, session, ctx, order, ct);
            return;
        }

        if (CommandParser.TryFindAdvanceInOrderText(message, out var typedAdvance) && message.Trim().Length <= 30 && ctx.PendingOrder is { } advanceDraft)
        {
            advanceDraft.AdvancePaid = typedAdvance;
            await SendDraftConfirmationAsync(seller, BuildConfirmationText(seller.PreferredLanguage, advanceDraft), ct);
            return;
        }

        if (CommandParser.TryParseDeliveryAmount(message, out var delivery) && ctx.PendingOrder is { } draft)
        {
            draft.DeliveryCharge = delivery;
            draft.Total = OrderTotal(draft.Subtotal, draft.DiscountAmount, delivery);
            await SendDraftConfirmationAsync(seller, BuildConfirmationText(seller.PreferredLanguage, draft), ct);
            return;
        }

        if (message.Trim().Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            ctx.PendingOrder = null;
            SetState(session, ConversationState.Idle);
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.012"), ct);
            return;
        }

        // Say again WHICH order Save would save: a bare "samajh nahi aaya" with Save / Edit / Cancel leaves the seller guessing what is waiting.
        if (ctx.PendingOrder is { } waiting)
        {
            await SendDraftConfirmationAsync(seller, MessageTemplates.Get("order-flow.056") + "\n\n" + BuildConfirmationText(seller.PreferredLanguage, waiting), ct);
            return;
        }
        await SendDraftConfirmationAsync(seller, "Maaf kijiye, yeh samajh nahi aaya.\n" + ConfirmTypedHint, ct);
    }

    /// <summary>
    /// The next likely things to do with a just-saved order, as taps. The labels are plain commands ("Receipt 12", "Mark 12 shipped", "Undo"),
    /// so a tapped button is parsed like typed text. This replaces the generic shortcut bar for this turn (the message is interactive).
    /// </summary>
    private Task SendOrderActionButtonsAsync(Seller seller, Order order, CancellationToken ct) =>
        _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("order-flow.013"),
            new[] { $"Receipt {order.Id}", $"Mark {order.Id} shipped", "Undo" }, ct);

    private static string SavedText(Seller seller, Order order, PendingOrderData pending) =>
        SavedText(seller, order) + (pending.CommentLeadNumber is { } leadNumber ? $"\nLead #{leadNumber} marked CONVERTED." : "");

    private static string SavedText(Seller seller, Order order) =>
        Lang.Normalize(seller.PreferredLanguage) == Lang.UrduScript
            ? $"✅ آرڈر محفوظ ہو گیا (#{order.Id})\nتبدیلی کے لیے: edit order {order.Id}"
            : (order.PaymentMethod == OrderPaymentMethod.Cod
                ? $"✅ Order saved as {Formatters.Status(order.Status)} (#{order.Id}) — COD."
                : $"✅ Order saved as {Formatters.Status(order.Status)} (#{order.Id}).") +
              $"\n✏️ Kuch badalna ho to: \"edit order {order.Id}\"";

    private async Task HandleMultiOrderConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var drafts = ctx.MultiOrders ?? new List<PendingOrderData>();
        List<PendingOrderData> chosen;
        if (CommandParser.IsAffirmative(message)) chosen = drafts;
        else if (int.TryParse(message.Trim().TrimEnd('.'), out var n) && n >= 1 && n <= drafts.Count) chosen = new() { drafts[n - 1] };
        else
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.014", drafts.Count), ct);
            return;
        }

        ctx.MultiOrders = null;
        SetState(session, ConversationState.Idle);
        var saved = new List<Order>();
        foreach (var draft in chosen) saved.Add(await SaveOrderFromDraftAsync(seller, draft, ct));
        await ReplyAsync(seller, saved.Count == 1
            ? MessageTemplates.Get("order-flow.036", saved[0].Id)
            : MessageTemplates.Get("order-flow.037", saved.Count, string.Join(", ", saved.Select(o => $"#{o.Id}"))), ct);
        foreach (var order in saved) await CheckLoyaltyThresholdAsync(seller, session, ctx, order, ct);
    }

    /// <summary>"1", "2", "2 lawn suit" or a bare price: the replies the product questions ask for.</summary>
    private static bool IsProductStepAnswer(string message) =>
        System.Text.RegularExpressions.Regex.IsMatch(message.Trim(), @"^(?:[12](?:\s+.+)?|(?:rs\.?\s*)?\d[\d,]*(?:\.\d+)?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>10 to 13 digits (03001234567, 3001234567, 923001234567, a landline with its code) and no letters; anything else is a typo.</summary>
    internal static bool IsPlausiblePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;
        var digits = phone.Count(char.IsDigit);
        return digits is >= 10 and <= 13 && !phone.Any(char.IsLetter);
    }

    /// <summary>"Chadar" and "chadar", "Lawn Suit" and "lawn suits": the same product named twice (case, a trailing s, one name inside the other).</summary>
    private static bool SameProductName(string a, string b)
    {
        static string Norm(string x) => string.Join(' ', x.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('s');
        var (x, y) = (Norm(a), Norm(b));
        return x == y || x.Contains(y) || y.Contains(x);
    }

    private static (string Name, string Own, string Said)? UnitMismatchIn(string text, IEnumerable<PendingOrderData> drafts)
    {
        foreach (var item in drafts.SelectMany(d => d.Items).Where(i => i.ProductId is not null))
        {
            var name = System.Text.RegularExpressions.Regex.Escape(item.ProductName.Trim());
            var m = System.Text.RegularExpressions.Regex.Match(text, $@"(?<!\d)\d{{1,3}}\s*(?<u>[^\W\d_]+)\s+{name}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(text, $@"\b{name}\s+\d{{1,3}}\s*(?<u>[^\W\d_]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || CommandParser.NormalizeUnit(m.Groups["u"].Value) is not { } said) continue;
            var own = CommandParser.NormalizeUnit(item.UnitType ?? "") ?? (item.UnitType ?? "piece").Trim().ToLowerInvariant();
            if (own != said) return (item.ProductName, own, said);
        }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex PhoneNotYetInOrder = new(
        @"\b(?:phone|number|nambar|mobile)\b[^.,;\n]{0,25}\b(?:nahi|nahin|nhi|baad|later|abhi)\b|\b(?:baad\s*mein|baad\s*me|later)\b[^.,;\n]{0,25}\b(?:phone|number)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex PhoneLaterWords = new(
        @"^\s*(?:skip|later|baad\s*mein|abhi\s*nahi|(?:phone|number|nambar|no)\b.{0,25}?\b(?:nahi|nahin|nhi|baad|later|skip)|.{0,40}\b(?:baad\s*mein|baad\s*me|bad\s*mein|later)\b.{0,30}(?:update|de\s*dun|dunga|dungi|bata|likh)|bina\s*(?:phone|number)|without\s*(?:phone|number))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task HandleOrderMissingFieldAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var pending = ctx.PendingOrder!;

        // "Chadar - 10000, cost 8500, stock 50" sent while the draft waits on "this product is not in the catalog": that IS the answer. The product is added with those details
        // and the order carries on from where it stopped (it is not dropped and the product line is not treated as a separate message).
        if (ctx.PendingMissingField is "ProductChoice" or "MappedProductName" or "NewProductPrice" && !string.IsNullOrWhiteSpace(ctx.PendingNewProductName)
            && CommandParser.TryParseProductLine(message.Trim(), out ProductLine? line) && line is not null
            && SameProductName(line.Name, ctx.PendingNewProductName!))
        {
            var added = new Product
            {
                SellerId = seller.Id, Name = ctx.PendingNewProductName!, Price = line.Price,
                UnitType = string.IsNullOrWhiteSpace(line.UnitType) ? "piece" : line.UnitType, UnitQty = line.UnitQty <= 0 ? 1 : line.UnitQty,
                CostPrice = line.Extras?.Cost, StockQty = line.Extras?.Stock, Category = line.Extras?.Category,
                Size = line.Extras?.Size, Color = line.Extras?.Color
            };
            _db.Products.Add(added);
            await _db.SaveChangesAsync(ct);
            ApplyResolvedProductToPendingItem(pending, ctx.PendingNewProductName!, new CatalogEntry(added, added.Name, Array.Empty<PriceTier>()));
            var details = new List<string> { Formatters.Money(line.Price) };
            if (line.Extras?.Cost is { } c) details.Add($"cost {Formatters.Money(c)}");
            if (line.Extras?.Stock is { } st) details.Add($"stock {st}");
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.057", added.Name, string.Join(", ", details)), ct);
            ctx.PendingNewProductName = null;
            ctx.PendingMissingField = null;
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
            return;
        }

        // A real command (e.g. "Dupatta - 600, cost 300") sent while the draft waits on "which product?" is not an answer: drop the draft and run it,
        // instead of searching the catalog for the command text over and over.
        if (ctx.PendingMissingField is "ProductChoice" or "MappedProductName" or "NewProductPrice"
            && !IsProductStepAnswer(message)
            && CommandParser.TryParse(message) is { } leave
            && (ctx.PendingMissingField != "MappedProductName" || FindCatalogEntry(await LoadCatalogAsync(seller, ct), message.Trim()) is null))
        {
            ctx.PendingOrder = null;
            ctx.PendingNewProductName = null;
            ctx.PendingMissingField = null;
            SetState(session, ConversationState.Idle);
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.015"), ct);
            await ExecuteCommandAsync(seller, session, ctx, leave, ct);
            return;
        }

        // A list of products and prices where the phone is asked is not a phone: leave the draft, as a real command does, and handle the list.
        if (ctx.PendingMissingField == "Phone" && CommandParser.LooksLikeProductPriceList(message.Trim()))
        {
            ResetFlowContext(ctx);
            SetState(session, ConversationState.Idle);
            await HandleIdleAsync(seller, session, ctx, message, ct);
            return;
        }

        // "cancel" is offered on the phone / name question, so it must work there; so must a whole new order message sent instead of the answer.
        // "Phone abhi nahi hai, baad mein update kar dunga" / "skip": save the order without a phone (it is added later with "<naam> ka phone 0300…").
        if (ctx.PendingMissingField == "Phone" && PhoneLaterWords.IsMatch(message.Trim()) && !System.Text.RegularExpressions.Regex.IsMatch(JoinSpacedPhones(message), @"\d{10}"))
        {
            pending.PhoneLater = true;
            ctx.PendingMissingField = null;
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
            return;
        }

        if (ctx.PendingMissingField is "Phone" or "CustomerName")
        {
            var text = message.Trim();
            var newOrderInstead = ctx.PendingMissingField == "Phone" && !IsPlausiblePhone(text)
                && System.Text.RegularExpressions.Regex.IsMatch(text, @"\p{L}{3,}") && (text.Any(char.IsDigit) || text.Contains(','));
            // "Ayesha 0300…, Bilal 0312…" names the waiting customers: those are phones, not a new order.
            if (newOrderInstead && System.Text.RegularExpressions.Regex.IsMatch(JoinSpacedPhones(text), @"\d{10}")
                && new[] { ctx.PendingOrder! }.Concat(ctx.QueuedOrders ?? new List<PendingOrderData>())
                    .Any(o => !string.IsNullOrWhiteSpace(o.CustomerName) && text.Contains(o.CustomerName.Trim(), StringComparison.OrdinalIgnoreCase)))
                newOrderInstead = false;
            // A name is a few plain words: digits or several lines mean the seller sent a whole new order ("Sara 0300 1234567 / Kurti 2"), not a name.
            if (ctx.PendingMissingField == "CustomerName" && (text.Any(char.IsDigit) || text.Contains('\n') || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5))
                newOrderInstead = true;
            if (OpensWithLeavePhrase(text) || CancelWords.Contains(text) || newOrderInstead)
            {
                ctx.PendingOrder = null;
                ctx.PendingMissingField = null;
                SetState(session, ConversationState.Idle);
                if (newOrderInstead)
                {
                    await HandleFreeformMessageAsync(seller, session, ctx, message, ct);
                    return;
                }
                await ReplyAsync(seller, MessageTemplates.Get("order-flow.015"), ct);
                return;
            }
        }

        switch (ctx.PendingMissingField)
        {
            case "ProductChoice":
            {
                // "1", or "2" optionally followed by the existing product's name ("2 lawn suit"): sellers often send both at once.
                var choice = System.Text.RegularExpressions.Regex.Match(message.Trim(), @"^([12])(?:\s+(.+))?$");
                if (choice.Success && choice.Groups[1].Value == "1" && !choice.Groups[2].Success)
                {
                    ctx.PendingMissingField = "NewProductPrice";
                    await ReplyAsync(seller, MessageTemplates.Get("order-flow.016", ctx.PendingNewProductName, PriceHowToText(ctx.PendingNewProductName!)), ct);
                    return;
                }
                if (choice.Success && choice.Groups[1].Value == "2")
                {
                    if (choice.Groups[2].Success)
                    {
                        var choiceCatalog = await LoadCatalogAsync(seller, ct);
                        var named = FindCatalogEntry(choiceCatalog, choice.Groups[2].Value);
                        if (named is not null)
                        {
                            ApplyResolvedProductToPendingItem(pending, ctx.PendingNewProductName!, named);
                            ctx.PendingNewProductName = null;
                            ctx.PendingMissingField = null;
                            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                            return;
                        }
                        ctx.PendingMissingField = "MappedProductName";
                        await ReplyAsync(seller, MessageTemplates.Get("order-flow.017", choice.Groups[2].Value), ct);
                        return;
                    }
                    ctx.PendingMissingField = "MappedProductName";
                    await ReplyAsync(seller, MessageTemplates.Get("order-flow.018"), ct);
                    return;
                }
                await ReplyAsync(seller, MessageTemplates.Get("order-flow.019"), ct);
                return;
            }

            case "NewProductPrice":
                if (!decimal.TryParse(message.Trim().Replace("Rs.", "", StringComparison.OrdinalIgnoreCase).Replace(",", ""), out var price))
                {
                    await ReplyAsync(seller, MessageTemplates.Get("order-flow.020"), ct);
                    return;
                }
                var product = new Product { SellerId = seller.Id, Name = ctx.PendingNewProductName!, Price = price };
                _db.Products.Add(product);
                await _db.SaveChangesAsync(ct);
                ApplyResolvedProductToPendingItem(pending, ctx.PendingNewProductName!, new CatalogEntry(product, product.Name, Array.Empty<PriceTier>()));
                await ReplyAsync(seller, MessageTemplates.Get("order-flow.021", ctx.PendingNewProductName, Formatters.Money(price)), ct);
                ctx.PendingNewProductName = null;
                ctx.PendingMissingField = null;
                await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                return;

            case "MappedProductName":
                // The reply offers "1" to add the unknown name as a new product: take it as that choice, not as a catalog search.
                if (message.Trim() == "1")
                {
                    ctx.PendingMissingField = "NewProductPrice";
                    await ReplyAsync(seller, MessageTemplates.Get("order-flow.016", ctx.PendingNewProductName, PriceHowToText(ctx.PendingNewProductName!)), ct);
                    return;
                }
                var catalog = await LoadCatalogAsync(seller, ct);
                var mapped = FindCatalogEntry(catalog, message.Trim());
                if (mapped is null)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("order-flow.022", message.Trim()), ct);
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
                // "Ayesha 0300 1111111, Bilal 0312 2222222": each phone goes to the order of the customer named just before it.
                var waitingOrders = new[] { pending }.Concat(ctx.QueuedOrders ?? new List<PendingOrderData>()).Where(o => string.IsNullOrWhiteSpace(o.Phone) && !o.PaidInFull).ToList();
                var namedPhones = System.Text.RegularExpressions.Regex.Matches(message.Replace("\n", ", "), @"(?<n>\p{L}[\p{L}. ]*?)\s*[:,\-]?\s*(?<p>\+?\d[\d \-]{8,16}\d)")
                    .Select(m => (Name: m.Groups["n"].Value.Trim(' ', ',', '.'), Phone: JoinSpacedPhones(m.Groups["p"].Value).Replace("-", "")))
                    .Where(x => IsPlausiblePhone(x.Phone)).ToList();
                if (namedPhones.Count >= 2 && namedPhones.Count <= waitingOrders.Count
                    && namedPhones.All(x => waitingOrders.Any(o => string.Equals(o.CustomerName?.Trim(), x.Name, StringComparison.OrdinalIgnoreCase))))
                {
                    foreach (var x in namedPhones)
                        waitingOrders.First(o => string.Equals(o.CustomerName?.Trim(), x.Name, StringComparison.OrdinalIgnoreCase)).Phone = x.Phone;
                    ctx.PendingMissingField = null;
                    await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                    return;
                }
                // Several customers in one message: "first one X, second Y" fills this order and the queued ones in the same order.
                var phones = System.Text.RegularExpressions.Regex.Matches(JoinSpacedPhones(message), @"(?<!\d)\d{10,13}(?!\d)").Select(m => m.Value).ToList();
                var needPhone = new[] { pending }.Concat(ctx.QueuedOrders ?? new List<PendingOrderData>()).Where(o => string.IsNullOrWhiteSpace(o.Phone) && !o.PaidInFull).ToList();
                if (phones.Count >= 2 && phones.Count <= needPhone.Count)
                {
                    for (var i = 0; i < phones.Count; i++) needPhone[i].Phone = phones[i];
                    ctx.PendingMissingField = null;
                    await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                    return;
                }
                if (!IsPlausiblePhone(message))
                {
                    await ReplyAsync(seller, MessageTemplates.Get("order-flow.023"), ct);
                    return;
                }
                pending.Phone = message.Trim();
                ctx.PendingMissingField = null;
                await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                return;

            case "PriceChoice":
            {
                var answer = message.Trim().TrimEnd('.');
                var item = pending.Items.FirstOrDefault(i => i.StatedPrice is not null);
                if (item is null) { ctx.PendingMissingField = null; await ContinueResolvingDraftAsync(seller, session, ctx, ct); return; }
                if (answer is not ("1" or "2" or "3"))
                {
                    await ReplyAsync(seller, MessageTemplates.Get("order-flow.024"), ct);
                    return;
                }
                if (answer is "1" or "3")
                {
                    item.UnitPrice = item.StatedPrice!.Value;
                    item.PriceDecided = true;
                }
                else item.WrittenPrice = null; // "2": the buyer's catalog rate applies from now on
                if (answer == "3" && item.ProductId is { } productId && await _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.SellerId == seller.Id, ct) is { } listed)
                {
                    listed.Price = item.StatedPrice!.Value;
                    await _db.SaveChangesAsync(ct);
                }
                item.StatedPrice = null;
                ctx.PendingMissingField = null;
                await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                return;
            }

            case "CustomerMatch":
            {
                var answer = message.Trim().TrimEnd('.');
                if (answer != "1" && answer != "2")
                {
                    await ReplyAsync(seller, MessageTemplates.Get("order-flow.025"), ct);
                    return;
                }
                if (answer == "1" && !string.IsNullOrWhiteSpace(ctx.PendingCustomerMatchName)) pending.CustomerName = ctx.PendingCustomerMatchName;
                pending.CustomerMatchResolved = true;
                ctx.PendingCustomerMatchName = null;
                ctx.PendingMissingField = null;
                await ContinueResolvingDraftAsync(seller, session, ctx, ct);
                return;
            }

            default:
                SetState(session, ConversationState.Idle);
                ctx.PendingOrder = null;
                await ReplyAsync(seller, MessageTemplates.Get("order-flow.026"), ct);
                return;
        }
    }

    /// <summary>
    /// A saved customer with this draft's phone (last 10 digits) and a related but different name: they share a name word ("Ali" / "Muhammad Ali").
    /// Null when a customer with exactly this name exists (that one is used, as before) or when the only phone match is an unrelated name (a shared family phone).
    /// </summary>
    private async Task<Customer?> FindRelatedCustomerByPhoneAsync(Seller seller, PendingOrderData pending, CancellationToken ct)
    {
        var digits = new string((pending.Phone ?? "").Where(char.IsDigit).ToArray());
        var name = pending.CustomerName?.Trim();
        if (digits.Length < 10 || string.IsNullOrEmpty(name)) return null;
        var last10 = digits[^10..];

        var sameName = name.ToLower();
        if (await _db.Customers.AnyAsync(c => c.SellerId == seller.Id && c.Name.ToLower() == sameName, ct)) return null;

        var candidates = await _db.Customers
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null && c.Phone != null && c.Phone.EndsWith(last10))
            .OrderBy(c => c.Id).Take(5).ToListAsync(ct);

        static HashSet<string> Words(string s) => s.Split(new[] { ' ', '.', ',', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2).Select(w => w.ToLowerInvariant()).ToHashSet();
        var wanted = Words(name);
        return candidates.FirstOrDefault(c => Words(c.Name).Overlaps(wanted));
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

        await ReplyAsync(seller, MessageTemplates.Get("order-flow.019"), ct);
    }

    private static PendingOrderData CopyHeader(PendingOrderData source) => new()
    {
        CustomerName = source.CustomerName,
        Phone = source.Phone,
        Address = source.Address,
        PaymentMethodText = source.PaymentMethodText,
        DiscountCode = source.DiscountCode,
        OrderSource = source.OrderSource,
        DeliveryCharge = source.DeliveryCharge,
        AdvancePaid = source.AdvancePaid,
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
            DeliveryCharge = pending.DeliveryCharge ?? seller.DefaultDeliveryCharge,
            Total = OrderTotal(pending.Subtotal, pending.DiscountAmount, pending.DeliveryCharge ?? seller.DefaultDeliveryCharge),
            SalesTaxRate = seller.SalesTaxRate,
            CreatedAt = pending.SoldAt ?? RecordTimeUtc()
        };
        if (pending.PaidInFull)
        {
            order.Status = OrderStatus.Delivered;
            order.PaymentStatus = PaymentStatus.Paid;
            order.AmountPaid = order.Total;
            order.PaidAt = order.CreatedAt;
        }
        else if (pending.AdvancePaid is > 0 and var advance)
        {
            order.AmountPaid = Math.Min(advance, order.Total);
            if (advance >= order.Total)
            {
                order.PaymentStatus = PaymentStatus.Paid;
                order.PaidAt = DateTime.UtcNow;
            }
        }

        foreach (var item in pending.Items)
        {
            var product = item.ProductId is { } pid
                ? await _db.Products.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Id == pid, ct)
                : await _db.Products.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Name == item.ProductName, ct);
            order.Items.Add(new OrderItem
            {
                ProductId = item.ProductId ?? product?.Id,
                ProductNameSnapshot = item.ProductName,
                UnitPrice = item.UnitPrice,
                UnitCost = product?.CostPrice,
                Quantity = item.Quantity
            });
        }

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);
        await ApplyStockChangeAsync(seller, new Dictionary<int, int>(), StockFootprint(order), ct);

        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id,
            ActionType = ActionType.OrderCreated,
            OrderId = order.Id,
            PayloadJson = "{}"
        });

        if (pending.CommentLeadId is { } leadId
            && await _db.CommentLeads.FirstOrDefaultAsync(l => l.Id == leadId && l.SellerId == seller.Id, ct) is { } lead)
        {
            lead.Status = "converted_to_order";
            lead.OrderId = order.Id;
        }

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
        // The receipt is the next thing most sellers need after a saved order: offer it as a tap.
        SuggestNext("receipt", "orders today");
        if (session.State == ConversationState.Idle) await MaybeSendSetupNudgeAsync(seller, ctx, ct);
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
            await ReplyAsync(seller, SavedText(seller, order, pending), ct);
            await SendOrderActionButtonsAsync(seller, order, ct);
            await AfterOrderSavedAsync(seller, session, ctx, order, ct);
            return;
        }

        if (choice == "2")
        {
            ctx.PendingOrder = null;
            SetState(session, ConversationState.Idle);
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.027"), ct);
            return;
        }

        if (CommandParser.TryParse(message) is { Kind: CommandKind.Help or CommandKind.Menu } command)
        {
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
            return;
        }

        await ReplyAsync(seller, MessageTemplates.Get("order-flow.019"), ct);
    }

    /// <summary>
    /// The AI words its clarification options freely ("category details", "inventory status"); when one clearly names a read-only report, the typed command that
    /// answers it, so a tap shows data instead of a neutral prompt. Never maps to anything that changes data.
    /// </summary>
    internal static string? ClarificationOptionCommand(string option)
    {
        static bool Has(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (Has(option, "category", "categories", "کیٹیگری") && !Has(option, "sale", "bikri", "سیل")) return MessageTemplates.Get("order-flow.042");
        if (Has(option, "category", "categories", "کیٹیگری")) return MessageTemplates.Get("order-flow.043");
        if (Has(option, "inventory", "stock", "اسٹاک")) return "stock";
        if (Has(option, "sales report", "sale report", "summary", "hisaab")) return "today's summary";
        if (Has(option, "latest products", "products", "catalog", "product list")) return "catalog";
        return null;
    }

    /// <summary>The AI writes the options freely, so the reply is picked from the option's wording; unrecognised wording gets a neutral prompt, never an order prompt.</summary>
    private static string ClarificationChoiceReply(string option)
    {
        static bool Has(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (Has(option, "status", "order update"))
            return MessageTemplates.Get("order-flow.044");
        if (Has(option, "order dena", "naya order", "order add", "new order"))
            return MessageTemplates.Get("order-flow.045");
        if (Has(option, "product", "poochna", "puchna", "sawal"))
            return MessageTemplates.Get("order-flow.046");
        return MessageTemplates.Get("order-flow.047");
    }

    // The AI sometimes offers the order draft's own buttons (Save / Edit / Cancel) as choices for an unrelated question. Those mean nothing here.
    internal static List<string> UsableClarificationOptions(IEnumerable<string> options)
    {
        var usable = options.Where(o => !ConfirmButtonLabels.Contains(o.Trim())).ToList();
        return usable.Count > 0 ? usable : DefaultClarificationOptions.ToList();
    }

    private async Task AskClarificationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string question, List<string> options, CancellationToken ct)
    {
        options = UsableClarificationOptions(options);
        ctx.ClarificationOptions = options;
        SetState(session, ConversationState.AwaitingClarificationChoice);
        // A tap sends the command itself when the option is one ("create discount"), otherwise its number (handled by HandleClarificationChoiceAsync).
        var choices = options.Select((o, i) => new ChoiceOption(o, CommandParser.TryParse(o) is not null ? o : (i + 1).ToString())).ToList();
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, question, choices, ct);
    }

    private async Task HandleClarificationChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var options = ctx.ClarificationOptions ?? DefaultClarificationOptions.ToList();

        if (CommandParser.TryParse(message) is { } command)
        {
            LearnPhrase(ctx, ctx.ClarifiedText, message);
            ctx.ClarifiedText = null;
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
            // Keyword-clarification options are typed commands: run the one picked.
            if ((CommandParser.TryParse(chosen) ?? CommandParser.TryParse(ClarificationOptionCommand(chosen) ?? "")) is { } chosenCommand)
            {
                LearnPhrase(ctx, ctx.ClarifiedText, CommandParser.TryParse(chosen) is not null ? chosen : ClarificationOptionCommand(chosen));
                ctx.ClarifiedText = null;
                await ExecuteCommandAsync(seller, session, ctx, chosenCommand, ct);
                return;
            }
            await ReplyAsync(seller, ClarificationChoiceReply(chosen), ct);
            return;
        }

        // Not a number: the seller moved on (e.g. pasted the actual order) — handle it as a fresh message.
        ctx.ClarificationOptions = null;
        SetState(session, ConversationState.Idle);
        await HandleFreeformMessageAsync(seller, session, ctx, message, ct);
    }

    /// <summary>
    /// Screen 26: when a new order is the customer's Nth (loyalty threshold), offer the loyalty discount on it.
    /// With no session (mid-queue / form submission) it only informs.
    /// </summary>
    private async Task CheckLoyaltyThresholdAsync(Seller seller, ConversationSession? session, SessionContextData ctx, Order order, CancellationToken ct)
    {
        var rule = await _db.LoyaltyRules.FirstOrDefaultAsync(r => r.SellerId == seller.Id && r.IsActive, ct);
        if (rule is null || rule.OrderThreshold <= 0) return;

        var orderCount = await _db.Orders.CountAsync(o => o.CustomerId == order.CustomerId && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned, ct);
        if (orderCount == 0 || orderCount % rule.OrderThreshold != 0) return;

        var customer = await _db.Customers.FindAsync(new object?[] { order.CustomerId }, ct);
        var text = MessageTemplates.Get("order-flow.052", customer?.Name, Formatters.Ordinal(orderCount), $"{rule.DiscountPercent:0.##}");
        if (session is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.028", text), ct);
            return;
        }

        ctx.LoyaltyOrderId = order.Id;
        ctx.LoyaltyDiscountPercent = rule.DiscountPercent;
        SetState(session, ConversationState.AwaitingLoyaltyDiscountConfirmation);
        await ReplyAsync(seller, MessageTemplates.Get("order-flow.029", text), ct);
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
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.030"), ct);
            return;
        }

        var order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.Id == orderId && o.SellerId == seller.Id, ct);
        if (order is null) return;

        var off = Math.Round((order.Total - order.DeliveryCharge) * percent / 100m, 0);
        order.DiscountAmount += off;
        order.Total = Math.Max(0, order.Total - off);
        order.DiscountCode ??= "LOYALTY";
        await ReplyAsync(seller, MessageTemplates.Get("order-flow.031", order.Customer?.Name, Formatters.Money(off), Formatters.Money(order.Total)), ct);
    }

    /// <summary>Screen 10d: the merchant relays how a buyer felt ("ayesha bahut khush thi order se").</summary>
    private async Task SaveCustomerFeedbackAsync(Seller seller, AiCustomerFeedback feedback, CancellationToken ct)
    {
        var lower = feedback.CustomerName.ToLower();
        var order = await _db.Orders.Include(o => o.Customer)
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.Name.ToLower() == lower)
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
            ? MessageTemplates.Get("order-flow.038", feedback.CustomerName)
            : MessageTemplates.Get("order-flow.039", order.Id, order.Customer?.Name), ct);
    }

    // Choice / confirmation states: a stray message should be understood, not matched against a word list. Left out: setup / guide, the reset and payment-subscription
    // confirmations (a wrong guess there costs more than a repeated question), the voice confirmation (own handler at the top) and the free-text modes (discount details,
    // business info, payment method, support reply edit, order edit, receipt design), whose answers can be anything and which already let a real command leave.
    private static readonly HashSet<ConversationState> AiRoutedStates = new()
    {
        ConversationState.AwaitingOrderConfirmation, ConversationState.AwaitingOrderMissingFields, ConversationState.AwaitingOrderGroupingChoice,
        ConversationState.AwaitingMultiOrderConfirmation, ConversationState.AwaitingClarificationChoice, ConversationState.AwaitingCancelConfirmation,
        ConversationState.AwaitingBulkStatusConfirmation, ConversationState.AwaitingDuplicateOrderConfirmation, ConversationState.AwaitingCodCollectedConfirmation,
        ConversationState.AwaitingRuntimeFilterChoice, ConversationState.AwaitingCustomDateRange, ConversationState.AwaitingBroadcastChannelChoice,
        ConversationState.AwaitingBroadcastAudienceChoice, ConversationState.AwaitingReceiptOrderChoice, ConversationState.AwaitingDeleteCustomerConfirmation, ConversationState.AwaitingLoyaltyDiscountConfirmation,
        ConversationState.AwaitingSupportReplyConfirmation, ConversationState.AwaitingSupportQueryPick,
        ConversationState.AwaitingImportConfirmation
    };

    // Free-text modes: the answer can be anything, so the AI is only asked about a long reply, and only a clear "new order" / "cancel" leaves the mode ("other" stays).
    private static readonly HashSet<ConversationState> AiRoutedFreeTextStates = new()
    {
        ConversationState.AwaitingDiscountDetails, ConversationState.AwaitingBusinessInfo, ConversationState.AwaitingSupportReplyEdit,
        ConversationState.AwaitingPaymentMethodInput, ConversationState.AwaitingOrderEdit, ConversationState.AwaitingReceiptDesign
    };

    /// <summary>
    /// The bot waits for an answer (any of <see cref="AiRoutedStates"/>) and the reply is not an obvious one (yes / no / a number / a phone / one or two words): the AI, told what the
    /// bot asked, says whether the reply answers it, is a new request (an order, a command, a question) or means "stop". Anything that is not an answer leaves the question and the
    /// message is handled as a fresh one, so nobody is trapped by a question or has their message swallowed. Money-changing steps still end in the usual YES; with no AI, or when it
    /// says "answer", the reply is handled exactly as before.
    /// </summary>
    private async Task<bool> TryRouteMidFlowReplyAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var freeText = AiRoutedFreeTextStates.Contains(session.State);
        if (!freeText && !AiRoutedStates.Contains(session.State)) return false;
        if (session.State == ConversationState.AwaitingOrderMissingFields && (ctx.PendingOrder is null
            || ctx.PendingMissingField is not ("Phone" or "CustomerName"))) return false;

        var text = message.Trim();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var obvious = IsPlausiblePhone(text) || CommandParser.IsAffirmative(text) || CommandParser.IsNegative(text) || CommandParser.IsAcknowledgement(text)
            || CancelWords.Contains(text) || (text.Length <= 3 && text.All(char.IsDigit))
            || (words <= 2 && !text.Any(char.IsDigit) && !text.Contains('\n') && ctx.PendingMissingField != "Phone")
            || Array.IndexOf(ConfirmButtonLabels, text) >= 0;
        if (obvious || (freeText && (words < 4 || CommandParser.TryParse(text) is not null))) return false;

        var route = await _ai.ClassifyMidFlowReplyAsync(DescribeVoiceSituation(seller, ctx), message, ct);
        if (route == "skip" && ctx.PendingMissingField == "Phone" && ctx.PendingOrder is { } noPhone)
        {
            noPhone.PhoneLater = true;
            ctx.PendingMissingField = null;
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
            return true;
        }
        if (route is not ("new_order" or "cancel" or "other") || (freeText && route == "other")) return false;
        LogNotUnderstood("router-" + route, session.State, message);

        // A side question or command in the middle of an order ("Ayesha ka phone kya hai?", "aaj ka hisaab") PAUSES the order: it is answered, then the order carries on.
        var pausedDraft = route == "other" && session.State is ConversationState.AwaitingOrderConfirmation or ConversationState.AwaitingOrderMissingFields
            && ctx.PendingOrder is not null
            ? (Draft: ctx.PendingOrder, Queued: ctx.QueuedOrders, State: session.State, Missing: ctx.PendingMissingField, NewName: ctx.PendingNewProductName)
            : default((PendingOrderData? Draft, List<PendingOrderData>? Queued, ConversationState State, string? Missing, string? NewName));
        _draftPutOnHold = pausedDraft.Draft is not null;

        ResetFlowContext(ctx);
        SetState(session, ConversationState.Idle);
        if (route == "cancel")
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.015"), ct);
            return true;
        }
        await HandleIdleAsync(seller, session, ctx, message, ct);

        // Nothing new was started by the side message: bring the paused order back and ask its next question again.
        if (pausedDraft.Draft is not null && session.State == ConversationState.Idle && ctx.PendingOrder is null)
        {
            ctx.PendingOrder = pausedDraft.Draft;
            ctx.QueuedOrders = pausedDraft.Queued;
            ctx.PendingMissingField = pausedDraft.Missing;
            ctx.PendingNewProductName = pausedDraft.NewName;
            SetState(session, pausedDraft.State);
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.058", pausedDraft.Draft.CustomerName ?? ""), ct);
            await ContinueResolvingDraftAsync(seller, session, ctx, ct);
        }
        return true;
    }
}
