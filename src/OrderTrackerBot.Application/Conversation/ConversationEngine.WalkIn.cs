using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Offline / counter sales: a buyer standing in the shop has no WhatsApp chat with the bot. "walkin Kurti 2, Dupatta", "counter sale 1500 bank",
// "offline sale Chai - 200 kal" make an order draft with the shared customer "Walk-in" (no phone asked), paid in full and delivered once the seller says YES.
public sealed partial class ConversationEngine
{
    internal const string WalkInCustomer = "Walk-in";
    private const decimal MaxWalkInAmount = 10_000_000m;

    internal sealed record WalkInItem(string Name, int Quantity, decimal? Price);
    internal sealed record WalkInSale(List<WalkInItem> Items, string? Payment, int DaysAgo, bool Confirm = false);

    // "walkin Kurti 1 then yes": a confirmation typed in the same message saves the draft right away.
    private static readonly Regex WalkInConfirm = new(@"(?:^|[\s,;.])(?:(?:then|phir|aur|and|or|fir)\s+)?(?:yes|haan|han|ha|ok|okay|confirm|theek\s+hai)\s*[.!]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WalkInDay = new(@"\b(?<d>kal|yesterday|parso|parson)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WalkInPay = new(@"\b(?<p>cash|naqd|bank|easypaisa|easy\s*paisa|jazz\s*cash|jazzcash|card|online)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WalkInAmountOnly = new(@"^(?:rs\.?\s*)?(?<p>\d[\d,]*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WalkInSplit = new(@"\s*(?:,|\+|;|\baur\b|\band\b)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WalkInPriced = new(@"^(?<n>.*[^\W\d_].*?)(?:\s+[-–]\s+|\s*[:=]\s*)(?:rs\.?\s*)?(?<p>\d[\d,]*)(?:\s*x\s*(?<q>\d+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WalkInQtyFirst = new(@"^(?<q>\d{1,3})\s*x?\s+(?<n>.*[^\W\d_].*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WalkInQtyLast = new(@"^(?<n>.*[^\W\d_].*?)\s*x?\s*(?<![\d])(?<q>\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static WalkInSale ParseWalkIn(string rest)
    {
        var text = rest.Trim();
        var confirm = false;
        text = Regex.Replace(text, @"[()]", " ").Trim();
        var days = 0;
        if (WalkInDay.Match(text) is { Success: true } day)
        {
            days = day.Groups["d"].Value.ToLowerInvariant().StartsWith("par") ? 2 : 1;
            text = WalkInDay.Replace(text, " ");
        }

        string? payment = null;
        if (WalkInPay.Match(text) is { Success: true } pay)
        {
            payment = pay.Groups["p"].Value.ToLowerInvariant();
            text = WalkInPay.Replace(text, " ");
        }
        text = Regex.Replace(text, @"\s+", " ").Trim(' ', ',', '+', ';');
        // The payment and day words are gone, so a trailing "yes" is now last.
        if (WalkInConfirm.Match(text) is { Success: true } yes && yes.Index > 0)
        {
            confirm = true;
            text = text[..yes.Index].Trim(' ', ',', ';', '.', '+');
        }

        var items = new List<WalkInItem>();
        if (WalkInAmountOnly.Match(text) is { Success: true } only)
        {
            if (decimal.TryParse(only.Groups["p"].Value.Replace(",", ""), out var amount) && amount is > 0 and <= MaxWalkInAmount) items.Add(new WalkInItem("Counter sale", 1, amount));
            return new WalkInSale(items, payment, days, confirm);
        }

        foreach (var part in WalkInSplit.Split(text).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            Match m;
            if ((m = WalkInPriced.Match(part)).Success && decimal.TryParse(m.Groups["p"].Value.Replace(",", ""), out var price) && price > 0)
                items.Add(new WalkInItem(m.Groups["n"].Value.Trim(), m.Groups["q"].Success ? int.Parse(m.Groups["q"].Value) : 1, price));
            else if ((m = WalkInQtyFirst.Match(part)).Success)
                items.Add(new WalkInItem(m.Groups["n"].Value.Trim(), int.Parse(m.Groups["q"].Value), null));
            else if ((m = WalkInQtyLast.Match(part)).Success)
                items.Add(new WalkInItem(m.Groups["n"].Value.Trim(), int.Parse(m.Groups["q"].Value), null));
            else if (part.Any(char.IsLetter))
                items.Add(new WalkInItem(part, 1, null));
        }
        // "Kurti -2" (a minus typed as a dash), a name ending in a dash, or an absurd price/quantity is a typo, not a sale line.
        static bool Junk(WalkInItem i) => i.Quantity <= 0 || i.Quantity > 999 || i.Price is > MaxWalkInAmount
            || i.Name.TrimEnd().EndsWith('-') || i.Name.TrimEnd().EndsWith('–') || i.Name.TrimEnd().EndsWith('=');
        return new WalkInSale(items.Where(i => !Junk(i)).ToList(), payment, days, confirm);
    }

    private async Task HandleWalkInSaleAsync(Seller seller, ConversationSession session, SessionContextData ctx, string rest, CancellationToken ct)
    {
        var sale = ParseWalkIn(rest);
        if (sale.Items.Count == 0)
        {
            await ReplyAsync(seller,
                "Counter sale aise likhein:\n• walkin Kurti 2, Dupatta\n• counter sale 1500 (sirf rakam)\n• offline sale Chai - 200 bank\n• walkin Kurti 2 kal (kal ki sale)\n" +
                "Payment: cash (default) / bank / easypaisa / jazzcash / card.", ct);
            return;
        }

        var catalog = await LoadCatalogAsync(seller, ct);
        var pending = BuildPendingFromWalkIn(catalog, sale);

        pending.CustomerName = WalkInCustomer;
        pending.OrderSource = "offline";
        pending.PaymentMethodText = sale.Payment ?? "cash";
        pending.PaidInFull = true;
        pending.DeliveryCharge = 0;
        if (sale.DaysAgo > 0) pending.SoldAt = RecordTimeUtc().AddDays(-sale.DaysAgo);

        ctx.PendingOrder = pending;
        await ContinueResolvingDraftAsync(seller, session, ctx, ct);
        if (sale.Confirm && session.State == ConversationState.AwaitingOrderConfirmation)
        {
            ctx.PendingOrderAskedAt ??= _clock.GetUtcNow().UtcDateTime; // a draft is only savable once its clock has started
            await HandleOrderConfirmationAsync(seller, session, ctx, "yes", ct);
        }
    }

    /// <summary>Items typed as "Kurti 2, Dupatta" / "Chai - 200" become draft lines: catalog items priced from the catalog, "name - price" items at the stated price.</summary>
    private static PendingOrderData BuildPendingFromWalkIn(List<CatalogEntry> catalog, WalkInSale sale)
    {
        var draft = new AiOrderDraft();
        foreach (var item in sale.Items.Where(i => i.Price is null))
            draft.Items.Add(new AiOrderItemDraft { ProductName = item.Name, Quantity = item.Quantity, MatchedCatalogProductName = FindCatalogEntry(catalog, item.Name)?.Label });
        var pending = BuildPendingOrder(draft, catalog, fromScreenshot: false);

        foreach (var item in sale.Items.Where(i => i.Price is not null))
        {
            var line = new PendingOrderItemData { ProductName = item.Name, Quantity = item.Quantity, UnitPrice = item.Price!.Value };
            if (FindCatalogEntry(catalog, item.Name) is { } entry)
            {
                ApplyCatalogEntry(line, entry);
                line.UnitPrice = item.Price.Value; // the price said at the counter wins over the list price
            }
            pending.Items.Add(line);
        }
        return pending;
    }

    private async Task HandleOrderForCustomerAsync(Seller seller, ConversationSession session, SessionContextData ctx, string name, CancellationToken ct)
    {
        var lower = name.ToLower();
        var all = await _db.Customers.Where(c => c.SellerId == seller.Id && c.DeletedAt == null && c.Name.ToLower().Contains(lower)).Take(10).ToListAsync(ct);
        var matches = all.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) matches = all;

        if (matches.Count == 0)
        {
            await ReplyAsync(seller, $"\"{name}\" naam ka customer saved nahi hai. Naye customer ka order seedha likhein, jaise: {name}: Kurti 2, 0300xxxxxxx", ct);
            return;
        }
        if (matches.Count > 1)
        {
            await AskClarificationAsync(seller, session, ctx, $"\"{name}\" se {matches.Count} customers milte hain — kis ke liye order?",
                matches.Take(9).Select(c => $"order for {c.Name}").ToList(), ct);
            return;
        }

        var customer = matches[0];
        ctx.OrderForCustomer = new OrderForCustomerData { Name = customer.Name, Phone = customer.Phone, Address = customer.Address, At = DateTime.UtcNow };
        var prompt = $"📝 {customer.Name} ke liye order — items likhein, jaise:\nKurti 2, Dupatta\nya: Lawn Suit - 3200\n(Phone/address pehle se saved hain.)";
        var usual = _features.SuggestionButtons ? await UsualItemsAsync(seller, customer, ct) : new List<ProductSuggestion>();
        ctx.ProductSuggestions = usual.Count > 0 ? usual : ctx.ProductSuggestions;
        if (usual.Count == 0)
        {
            await ReplyAsync(seller, prompt, ct);
            return;
        }
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            prompt + $"\n\n🔁 {customer.Name} ki pichli chizen — ek tap se 1 add hoga:", usual.Select(u => u.Title).ToList(), ct);
    }

    /// <summary>
    /// The customer's usual items: the products most often bought in her last 3 orders (cancelled and returned ones do not count; ties go to the more recent order),
    /// at most 3, only products that are still active in the catalog, titled "Name Rs.price" like the did-you-mean taps. Plain database history, no AI.
    /// </summary>
    private async Task<List<ProductSuggestion>> UsualItemsAsync(Seller seller, Customer customer, CancellationToken ct)
    {
        var orders = await _db.Orders.Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CustomerId == customer.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .OrderByDescending(o => o.CreatedAt).Take(3).ToListAsync(ct);
        if (orders.Count == 0) return new List<ProductSuggestion>();

        var catalog = await LoadCatalogAsync(seller, ct);
        var ranked = orders.SelectMany((o, recency) => o.Items.Where(i => i.ProductId != null).Select(i => (i.ProductId!.Value, recency)))
            .GroupBy(x => x.Item1)
            .Select(g => (ProductId: g.Key, Count: g.Count(), Recent: g.Min(x => x.recency)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Recent)
            .Select(x => catalog.FirstOrDefault(c => c.Product.Id == x.ProductId)?.Label)
            .Where(label => label is not null).Select(label => label!)
            .Take(3).ToList();
        return BuildSuggestions(catalog, ranked);
    }

    /// <summary>The message after "order for Sara": read as her items. A real command, or anything that is not items, drops the request and runs as usual.</summary>
    private async Task<bool> TryHandleOrderForCustomerItemsAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.OrderForCustomer is not { } target) return false;
        ctx.OrderForCustomer = null;
        if (DateTime.UtcNow - target.At > TimeSpan.FromMinutes(10)) return false;
        if (CommandParser.TryParse(message) is not null || CommandParser.IsAffirmative(message) || CommandParser.IsNegative(message)) return false;

        var sale = ParseWalkIn(message);
        if (sale.Items.Count == 0 || sale.Items.Any(i => i.Name == "Counter sale")) return false;

        var pending = BuildPendingFromWalkIn(await LoadCatalogAsync(seller, ct), sale);
        pending.CustomerName = target.Name;
        pending.Phone = target.Phone;
        pending.Address = target.Address;
        pending.PaymentMethodText = sale.Payment;
        pending.CustomerMatchResolved = true; // she is the saved customer: no "same customer?" question
        ctx.PendingOrder = pending;
        await ContinueResolvingDraftAsync(seller, session, ctx, ct);
        return true;
    }
}
