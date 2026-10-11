using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// "edit order 12": change a saved order one instruction at a time — item quantity/price, remove/add items, the customer's
// phone/address/name, delivery and payment method — until "done". The first change logs a snapshot so "undo" restores it all.
public partial class ConversationEngine
{
    private const string OrderEditHelp =
        "Kya badalna hai? (aik waqt mein aik baat)\n" +
        "• \"1 = 3\" — item 1 ki tadaad 3\n" +
        "• \"price 1 = 1500\" — item 1 ka rate\n" +
        "• \"remove 2\" — item 2 hatao\n" +
        "• \"add Kurti 2\" — naya item\n" +
        "• \"phone 0300…\" / \"address …\" / \"name …\"\n" +
        "• \"delivery 250\" / \"free delivery\"\n" +
        "• \"payment cod\" / \"payment jazzcash\"\n" +
        "Mukammal ho to \"done\". Ghalti ho jaye to baad mein \"undo\".";

    private sealed record EditSnapshotItem(int? ProductId, string Name, decimal UnitPrice, int Quantity, decimal? UnitCost = null);
    private sealed record EditSnapshot(List<EditSnapshotItem> Items, decimal Subtotal, decimal DiscountAmount, decimal DeliveryCharge,
        decimal Total, OrderPaymentMethod PaymentMethod, string? CustomerName, string? CustomerPhone, string? CustomerAddress);

    private async Task StartOrderEditAsync(Seller seller, ConversationSession session, SessionContextData ctx, int? number, CancellationToken ct)
    {
        Order? order;
        var fromList = false;
        if (number is { } n)
        {
            var (orderId, listed) = ResolveListNumber(ctx, n);
            fromList = listed;
            order = await LoadOrderForEditAsync(seller, orderId, ct);
        }
        else
        {
            var latestId = await _db.Orders.Where(o => o.SellerId == seller.Id).OrderByDescending(o => o.CreatedAt).Select(o => (int?)o.Id).FirstOrDefaultAsync(ct);
            order = latestId is null ? null : await LoadOrderForEditAsync(seller, latestId.Value, ct);
        }

        if (order is null)
        {
            await ReplyAsync(seller, number is null ? "Abhi koi order nahi hai." : $"Order #{number} nahi mila.", ct);
            return;
        }
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await ReplyAsync(seller, $"Order #{order.Id} {Formatters.Status(order.Status)} hai — isay badla nahi ja sakta.", ct);
            return;
        }

        ctx.EditOrderId = order.Id;
        ctx.EditSnapshotLogged = false;
        ctx.EditDiscardPending = false;
        ctx.EditBaselineLogId = await _db.ActionLogs.Where(a => a.SellerId == seller.Id && a.OrderId == order.Id && a.ActionType == ActionType.OrderEdited)
            .OrderByDescending(a => a.Id).Select(a => a.Id).FirstOrDefaultAsync(ct);
        SetState(session, ConversationState.AwaitingOrderEdit);
        await ReplyAsync(seller,
            (number is null ? "(Aakhri order)\n" : "") + (fromList ? $"(\"{number}\" aapki last list ka number tha)\n" : "") +
            $"{OrderEditSummary(order)}\n\n{OrderEditHelp}", ct);
    }

    // Once an order has gone out, its items, rates and delivery charge are fixed: the receipt, the stock and the money must keep matching.
    // Recording a payment stays allowed (COD cash often arrives after dispatch), and so do customer details and the payment method.
    public static bool IsDispatched(Order order) => order.Status is OrderStatus.Shipped or OrderStatus.Delivered;

    private static string DispatchedLockText(Order order) =>
        $"Order #{order.Id} {Formatters.Status(order.Status)} hai — items, rate ya delivery nahi badal sakte. " +
        $"Payment ke liye \"order {order.Id} advance 500\" likhein.";

    private Task<Order?> LoadOrderForEditAsync(Seller seller, int orderId, CancellationToken ct) =>
        _db.Orders.Include(o => o.Customer).Include(o => o.Items).FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);

    private async Task HandleOrderEditAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var order = ctx.EditOrderId is { } id ? await LoadOrderForEditAsync(seller, id, ct) : null;
        if (order is null)
        {
            EndOrderEdit(session, ctx);
            await HandleIdleAsync(seller, session, ctx, message, ct);
            return;
        }
        // A pending "jaari or chhoro?" question is answered by the next message, whatever it is.
        var discardPending = ctx.EditDiscardPending;
        ctx.EditDiscardPending = false;

        if (!CommandParser.TryParseOrderEdit(message, out var change))
        {
            // Acknowledgements, refusals and cancels are read before anything else: they must never fall into the help text or leave edit mode by accident.
            if (await TryHandleEditModeWordsAsync(seller, session, ctx, order, message, discardPending, ct)) return;

            // Anything else that is a real command leaves edit mode and runs ("undo", "menu", "orders today"...).
            if (CommandParser.TryParse(message) is { } command)
            {
                EndOrderEdit(session, ctx);
                await ExecuteCommandAsync(seller, session, ctx, command, ct);
                return;
            }
            await ReplyAsync(seller, $"Samajh nahi aaya.\n\n{OrderEditHelp}", ct);
            return;
        }

        if (change.Kind == "done")
        {
            EndOrderEdit(session, ctx);
            await ReplyAsync(seller, $"✅ Order #{order.Id} save ho gaya.\n{OrderEditSummary(order)}", ct);
            return;
        }

        if (IsDispatched(order) && change.Kind is ("qty" or "price" or "remove" or "add" or "delivery"))
        {
            await ReplyAsync(seller, DispatchedLockText(order), ct);
            return;
        }

        var before = Snapshot(order);
        var previousTotal = order.Total;
        var stockBefore = StockFootprint(order);
        var (ok, result) = await ApplyOrderEditAsync(seller, order, change, ct);
        if (!ok)
        {
            await ReplyAsync(seller, $"⚠️ {result}", ct);
            return;
        }

        if (!ctx.EditSnapshotLogged)
        {
            _db.ActionLogs.Add(new ActionLog
            {
                SellerId = seller.Id, ActionType = ActionType.OrderEdited, OrderId = order.Id,
                PayloadJson = JsonSerializer.Serialize(before)
            });
            ctx.EditSnapshotLogged = true;
        }

        await RecalculateOrderTotalsAsync(seller, order, ct);
        await ApplyStockChangeAsync(seller, stockBefore, StockFootprint(order), ct);
        var paidWarning = order.PaymentStatus == PaymentStatus.Paid && order.Total != previousTotal
            ? $"\n⚠️ Payment PAID thi ({Formatters.Money(previousTotal)}) — farq ka hisaab khud rakhein."
            : "";
        await ReplyAsync(seller, $"✅ {result}{paidWarning}\n\n{OrderEditSummary(order)}\n\nAur kuch? Warna \"done\".", ct);
    }

    private static void EndOrderEdit(ConversationSession session, SessionContextData ctx)
    {
        ctx.EditOrderId = null;
        ctx.EditSnapshotLogged = false;
        ctx.EditDiscardPending = false;
        ctx.EditBaselineLogId = 0;
        SetState(session, ConversationState.Idle);
    }

    private const RegexOptions EditWordOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    // Replies to the last message: they confirm nothing, and they never approve the order.
    // Acknowledgements only: in edit mode they never save the order, so the Punjabi/Roman Urdu forms are safe to accept here.
    private static readonly Regex EditAcknowledgement = new(
        @"^(ok|okay|shukriya|shukria|shukriya\s+ji|thanks|thank\s+you|ji|jee|jee\s+bilkul|bilkul|acha|acha\s+ji|achha|achha\s+ji|accha|accha\s+ji|theek(\s+(hai|ae))?|thik(\s+hai)?|theek\s+ae|changa(\s+ji)?|sahi\s+ae|koi\s+gal\s+nai|chalo\s+ji)[.!]*$|^ٹھیک\s+ہے[.!]*$", EditWordOptions);
    // "jaari" = keep editing; "chhoro" / "cancel karo" = discard this edit. Only these answer the "jaari or chhoro?" question.
    private static readonly Regex EditKeepWords = new(@"^(jaari|jari|jaari\s+rakho|jaari\s+rakhein|jari\s+rakho|jari\s+rakhein|edit\s+jaari\s+rakho|edit\s+jaari\s+rakhein|continue|rakhein|rakho)[.!]*$", EditWordOptions);
    private static readonly Regex EditDiscardWords = new(
        @"^(chhoro|chhodo|chhod\s+do|chhor\s+do|discard|cancel|cancel\s+karo|cancel\s+kar\s+do|cancel\s+kardo|cancel\s+karein|cancel\s+krdo|cancel\s+kar\s+dein|khatam\s+karo)[.!]*$", EditWordOptions);
    // "nahi cancel karo": a refusal in front of the instruction does not undo it. "nahi" after the instruction does.
    private static readonly Regex EditNegatedCancel = new(
        @"^(nahi|nahin|no)[\s,.!]*(bhai|yar|ji|sir|please|bas)?[\s,.!]*(cancel|chhoro|chhodo)\b(?!.*\b(mat|nahi|nahin|na|nai)\b)", EditWordOptions);
    // "cancel mat karo", "cancel nahi karna": a cancel with a negation after it keeps the draft open.
    private static readonly Regex EditCancelRefused = new(@"\b(cancel|chhoro|chhodo|discard)\b.*\b(mat|nahi|nahin|na|nai)\b", EditWordOptions);
    private static readonly Regex EditRefusal = new(@"^(nahi|nahin|nahee|nai|nhi|no)[.!]*$", EditWordOptions);

    /// <summary>
    /// The words that are not an edit instruction, read before the edit grammar and the command router: acknowledgements, refusals, cancels, and a
    /// correction ("3500 nahi, 5300"). Returns false for anything else. Never approves an order.
    /// </summary>
    private async Task<bool> TryHandleEditModeWordsAsync(Seller seller, ConversationSession session, SessionContextData ctx, Order order, string message,
        bool discardPending, CancellationToken ct)
    {
        var text = message.Trim();
        if (discardPending)
        {
            if (EditKeepWords.IsMatch(text)) { await ReplyAsync(seller, KeepEditingText, ct); return true; }
            if (EditDiscardWords.IsMatch(text) || EditNegatedCancel.IsMatch(text)) { await DiscardOrderEditAsync(seller, session, ctx, order, ct); return true; }
        }

        if (EditDiscardWords.IsMatch(text) || EditNegatedCancel.IsMatch(text))
        {
            await DiscardOrderEditAsync(seller, session, ctx, order, ct);
            return true;
        }
        if (EditCancelRefused.IsMatch(text))
        {
            await ReplyAsync(seller, $"Theek hai — cancel nahi kiya. {KeepEditingText}", ct);
            return true;
        }
        if (EditRefusal.IsMatch(text))
        {
            // "Nahi" can mean keep or drop the changes: ask, and do nothing until the seller says which.
            ctx.EditDiscardPending = true;
            await ReplyAsync(seller, "Kya karna hai?\n• \"jaari\" — edit jaari rakhein\n• \"chhoro\" — ye badlaav discard karein (order pehli halat mein)\nJo chunein, woh likh dein.", ct);
            return true;
        }
        if (CommandParser.IsAcknowledgement(text) || EditAcknowledgement.IsMatch(text))
        {
            await ReplyAsync(seller, $"👍 Theek hai. {KeepEditingText}", ct);
            return true;
        }
        // "haan" answers an order-placement question, and none is pending while editing: it saves nothing.
        if (CommandParser.IsAffirmative(text))
        {
            await ReplyAsync(seller, $"Abhi order edit ho raha hai, is liye \"haan\" se save nahi hoga. {KeepEditingText}", ct);
            return true;
        }
        return await TryApplyEditCorrectionAsync(seller, session, ctx, order, text, ct);
    }

    private const string KeepEditingText =
        "Edit jaari hai — aur badlaav likhein (jaise \"1 = 3\"), ya \"done\" likh kar save karein.";

    /// <summary>
    /// "3500 nahi, 5300" while editing: the new amount replaces the one taken back, but only when exactly one field holds the old amount.
    /// Otherwise the seller is asked which field, and nothing changes.
    /// </summary>
    private static readonly Regex CorrectionPriceWord = new(@"\b(rate|price|rates|qeemat|daam|kimat)\b", RegexOptions.IgnoreCase);
    private static readonly Regex CorrectionDeliveryWord = new(@"\b(delivery|delivery\s+charge|kiraya)\b", RegexOptions.IgnoreCase);
    private static readonly Regex CorrectionOtherWord = new(@"\b(discount|total|quantity|qty|pieces?|pcs|coupon|advance|paid)\b", RegexOptions.IgnoreCase);

    private async Task<bool> TryApplyEditCorrectionAsync(Seller seller, ConversationSession session, SessionContextData ctx, Order order, string text, CancellationToken ct)
    {
        var retracted = SpokenNumbers.RetractedAmounts(text);
        if (retracted.Count != 1) return false;
        var replacements = SpokenNumbers.Amounts(text).Where(a => !retracted.Contains(a)).ToList();
        if (replacements.Count != 1) return false;

        var old = (decimal)retracted.Single();
        var value = replacements[0];
        var priceMatches = order.Items.OrderBy(i => i.Id).Select((item, index) => (Number: index + 1, Item: item))
            .Where(x => x.Item.UnitPrice == old).ToList();
        var deliveryMatches = order.DeliveryCharge == old;

        var matches = priceMatches.Count + (deliveryMatches ? 1 : 0);
        // The seller's own words must agree with the one field we would change. "delivery", "discount" or "total" are not a field we change here.
        var named = new HashSet<string>();
        if (CorrectionPriceWord.IsMatch(text)) named.Add("price");
        if (CorrectionDeliveryWord.IsMatch(text)) named.Add("delivery");
        if (CorrectionOtherWord.IsMatch(text)) named.Add("other");
        var target = priceMatches.Count == 1 ? "price" : "delivery";
        var namesSameField = named.Count == 0 || (named.Count == 1 && named.Single() == target);

        if (matches == 1 && namesSameField)
        {
            var instruction = priceMatches.Count == 1 ? $"price {priceMatches[0].Number} = {value}" : $"delivery {value}";
            await HandleOrderEditAsync(seller, session, ctx, instruction, ct);
            return true;
        }
        if (matches == 0)
        {
            await ReplyAsync(seller, $"Rs.{old:0.##} is order mein kahin nahi milta. Likhein: \"price 1 = {value}\" (item ka rate) ya \"delivery {value}\".", ct);
            return true;
        }

        var options = priceMatches.Select(x => $"• \"price {x.Number} = {value}\" — {x.Item.ProductNameSnapshot} ka rate")
            .Concat(deliveryMatches ? new[] { $"• \"delivery {value}\" — delivery charge" } : Array.Empty<string>());
        await ReplyAsync(seller, $"Kaunsi cheez badlni hai? Rs.{old:0.##} in mein se kahin hai:\n{string.Join("\n", options)}", ct);
        return true;
    }

    /// <summary>"cancel karo" while editing: the edits go, the order stays. Cancelling the saved order needs its own "cancel order N".</summary>
    private async Task DiscardOrderEditAsync(Seller seller, ConversationSession session, SessionContextData ctx, Order order, CancellationToken ct)
    {
        var edited = ctx.EditSnapshotLogged;
        if (edited)
        {
            var log = await _db.ActionLogs.Where(a => a.SellerId == seller.Id && a.OrderId == order.Id && a.ActionType == ActionType.OrderEdited
                    && !a.Undone && a.Id > ctx.EditBaselineLogId)
                .OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
            if (log is not null)
            {
                log.Undone = true;
                await UndoOrderEditAsync(seller, log, ct);
            }
        }
        EndOrderEdit(session, ctx);
        await ReplyAsync(seller,
            (edited ? $"✖️ Edit cancel kar diya — Order #{order.Id} ke badlaav wapas ho gaye." : $"✖️ Edit band kar diya — Order #{order.Id} mein koi badlaav nahi hua.") +
            $"\nOrder cancel nahi hua. Order cancel karne ke liye \"cancel order {order.Id}\" likhein.", ct);
    }

    private async Task<(bool Ok, string Message)> ApplyOrderEditAsync(Seller seller, Order order, OrderEditInstruction change, CancellationToken ct)
    {
        var items = order.Items.OrderBy(i => i.Id).ToList();
        OrderItem? Item(int? number) => number is >= 1 && number <= items.Count ? items[number.Value - 1] : null;
        string NoItem(int? number) => $"Item {number} nahi hai — 1 se {items.Count} tak number likhein.";

        switch (change.Kind)
        {
            case "qty":
            {
                if (Item(change.Item) is not { } item) return (false, NoItem(change.Item));
                if (change.Quantity is not > 0) return (false, "Tadaad kam az kam 1 honi chahiye — hatana ho to \"remove " + change.Item + "\".");
                item.Quantity = change.Quantity.Value;
                return (true, $"{item.ProductNameSnapshot} ki tadaad {item.Quantity} kar di.");
            }
            case "price":
            {
                if (Item(change.Item) is not { } item) return (false, NoItem(change.Item));
                item.UnitPrice = change.Amount!.Value;
                return (true, $"{item.ProductNameSnapshot} ka rate {Formatters.Money(item.UnitPrice)} kar diya.");
            }
            case "remove":
            {
                if (Item(change.Item) is not { } item) return (false, NoItem(change.Item));
                if (items.Count == 1) return (false, $"Yeh aakhri item hai — poora order hatana ho to \"cancel order {order.Id}\".");
                order.Items.Remove(item);
                _db.OrderItems.Remove(item);
                return (true, $"{item.ProductNameSnapshot} hata diya.");
            }
            case "add":
            {
                var catalog = await LoadCatalogAsync(seller, ct);
                var entry = FindCatalogEntry(catalog, change.Text!)
                            ?? catalog.FirstOrDefault(c => c.Product.Name.Contains(change.Text!, StringComparison.OrdinalIgnoreCase));
                if (entry is null) return (false, $"\"{change.Text}\" catalog mein nahi mila. Pehle add karein: \"{change.Text} - price\"");
                var line = new PendingOrderItemData { ProductName = entry.Product.Name, Quantity = change.Quantity ?? 1 };
                ApplyCatalogEntry(line, entry);
                order.Items.Add(new OrderItem { ProductId = entry.Product.Id, ProductNameSnapshot = entry.Product.Name, UnitPrice = line.UnitPrice, UnitCost = entry.Product.CostPrice, Quantity = line.Quantity });
                return (true, $"{entry.Product.Name} x{line.Quantity} add kar diya ({Formatters.Money(line.UnitPrice)} each).");
            }
            case "phone":
                order.Customer!.Phone = change.Text;
                return (true, $"Phone {change.Text} kar diya (customer record mein bhi).");
            case "address":
                order.Customer!.Address = change.Text;
                return (true, "Address update ho gaya (customer record mein bhi).");
            case "name":
                order.Customer!.Name = change.Text!;
                return (true, $"Customer ka naam \"{change.Text}\" kar diya.");
            case "delivery":
                order.DeliveryCharge = change.Amount!.Value;
                return (true, change.Amount > 0 ? $"Delivery {Formatters.Money(change.Amount.Value)} kar di." : "Free delivery kar di.");
            case "payment":
                order.PaymentMethod = ResolvePaymentMethod(change.Text);
                return (true, $"Payment: {PaymentLabel(order.PaymentMethod, change.Text)}.");
            default:
                return (false, OrderEditHelp);
        }
    }

    /// <summary>Subtotal from the items; a percent discount code is re-applied to the new subtotal, a flat one is capped by it.</summary>
    private async Task RecalculateOrderTotalsAsync(Seller seller, Order order, CancellationToken ct)
    {
        order.Subtotal = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        if (order.DiscountCode is { } code && code != "LOYALTY"
            && await _db.Discounts.FirstOrDefaultAsync(d => d.SellerId == seller.Id && d.Code == code, ct) is { Type: DiscountType.Percent } percent)
            order.DiscountAmount = Math.Round(order.Subtotal * percent.Value / 100m, 2);
        order.DiscountAmount = Math.Min(order.DiscountAmount, order.Subtotal);
        order.Total = OrderTotal(order.Subtotal, order.DiscountAmount, order.DeliveryCharge);
    }

    private static EditSnapshot Snapshot(Order order) => new(
        order.Items.OrderBy(i => i.Id).Select(i => new EditSnapshotItem(i.ProductId, i.ProductNameSnapshot, i.UnitPrice, i.Quantity, i.UnitCost)).ToList(),
        order.Subtotal, order.DiscountAmount, order.DeliveryCharge, order.Total, order.PaymentMethod,
        order.Customer?.Name, order.Customer?.Phone, order.Customer?.Address);

    /// <summary>Undo of a whole edit session: puts items, amounts, payment method and the customer's details back.</summary>
    private async Task UndoOrderEditAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        var order = log.OrderId is { } id ? await LoadOrderForEditAsync(seller, id, ct) : null;
        var snapshot = JsonSerializer.Deserialize<EditSnapshot>(log.PayloadJson);
        if (order is null || snapshot is null) return;

        var stockBefore = StockFootprint(order);
        foreach (var item in order.Items.ToList())
        {
            order.Items.Remove(item);
            _db.OrderItems.Remove(item);
        }
        foreach (var item in snapshot.Items)
            order.Items.Add(new OrderItem { ProductId = item.ProductId, ProductNameSnapshot = item.Name, UnitPrice = item.UnitPrice, UnitCost = item.UnitCost, Quantity = item.Quantity });
        order.Subtotal = snapshot.Subtotal;
        order.DiscountAmount = snapshot.DiscountAmount;
        order.DeliveryCharge = snapshot.DeliveryCharge;
        order.Total = snapshot.Total;
        order.PaymentMethod = snapshot.PaymentMethod;
        await ApplyStockChangeAsync(seller, stockBefore, StockFootprint(order), ct);
        if (order.Customer is { } customer)
        {
            customer.Name = snapshot.CustomerName ?? customer.Name;
            customer.Phone = snapshot.CustomerPhone;
            customer.Address = snapshot.CustomerAddress;
        }
        await ReplyAsync(seller, $"↩️ Reverted — Order #{order.Id} ki tabdeeliyan wapas.\n{OrderEditSummary(order)}", ct);
    }

    private static string OrderEditSummary(Order order)
    {
        var lines = new List<string>
        {
            $"✏️ Order #{order.Id} — {order.Customer?.Name}" +
            (string.IsNullOrWhiteSpace(order.Customer?.Phone) ? "" : $" ({order.Customer!.Phone})")
        };
        if (!string.IsNullOrWhiteSpace(order.Customer?.Address)) lines.Add($"📍 {order.Customer!.Address}");
        lines.AddRange(order.Items.OrderBy(i => i.Id).Select((i, n) => $"{n + 1}. {i.ProductNameSnapshot} x{i.Quantity} — {Formatters.Money(i.UnitPrice * i.Quantity)}"));
        if (order.DiscountAmount > 0) lines.Add($"Discount{(order.DiscountCode is null ? "" : $" ({order.DiscountCode})")}: -{Formatters.Money(order.DiscountAmount)}");
        if (order.DeliveryCharge > 0) lines.Add($"Delivery: {Formatters.Money(order.DeliveryCharge)}");
        lines.Add($"Total: {Formatters.Money(order.Total)} · Payment: {PaymentLabel(order.PaymentMethod, null)} · {Formatters.Status(order.Status)}");
        return string.Join("\n", lines);
    }
}
