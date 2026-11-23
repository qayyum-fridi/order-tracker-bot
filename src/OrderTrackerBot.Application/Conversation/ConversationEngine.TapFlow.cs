using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// The daily jobs by tapping, for a seller who knows no commands and with no AI needed:
// - a new order: tap a customer (or "Naya customer" and type the name), then tap a product, then Save;
// - "Add product" / "Kharcha likhna" / "Tankhwah dena": the very next plain message is that entry ("lawn suit 4500", "kiraya 3000", "Ali 1500");
// - "Paisa lena hai": the unpaid list, each order a tap that marks it paid.
public partial class ConversationEngine
{
    /// <summary>Row id of "➕ Naya customer" in the new-order picker: the command that starts an order for a buyer not saved yet.</summary>
    internal const string NewCustomerForOrderRowId = "naya customer";
    private const int ListRowTitleMax = 24;
    private const int PickerCustomers = 7;
    private const int PickerProducts = 9;
    private const int PickerUnpaid = 9;

    private static readonly Regex TapAmount = new(@"(?<![\d.])(\d{1,3}(?:,\d{3})+|\d+)(?![\d.])", RegexOptions.Compiled);

    /// <summary>"Naya order": the ways to write one, then the saved customers to tap (most recent buyers first) and "Naya customer".</summary>
    private async Task SendNewOrderPickerAsync(Seller seller, SessionContextData ctx, CancellationToken ct)
    {
        // No products yet: a tap-by-tap order would end on an empty list, so the first step is adding products (typing the order still works).
        if (!await _db.Products.AnyAsync(p => p.SellerId == seller.Id && p.IsActive, ct))
        {
            await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
                MessageTemplates.Get("tapflow.order.nocatalog", Industry(seller).OrderExample), new[] { "add product", "catalog" }, ct);
            return;
        }

        var customers = await _db.Customers.AsNoTracking()
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null)
            .Select(c => new { c.Id, c.Name, Last = _db.Orders.Where(o => o.CustomerId == c.Id).Max(o => (DateTime?)o.CreatedAt) })
            .OrderByDescending(c => c.Last ?? DateTime.MinValue).ThenByDescending(c => c.Id)
            .Take(PickerCustomers).ToListAsync(ct);

        var customerRows = customers
            .Select(c => new MenuRow($"order for {c.Name}", ShortenChoice("👤 " + c.Name, ListRowTitleMax)))
            .Append(new MenuRow(NewCustomerForOrderRowId, "➕ Naya customer"))
            .ToArray();
        var sections = new[]
        {
            new MenuSection("Customer chunein", customerRows),
            new MenuSection("Aur", new[] { new MenuRow("orders today", "📦 Aaj ke orders"), BackToMenu })
        };
        await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber,
            MessageTemplates.Get("tapflow.order.pick", NewOrderHelpTextFor(seller)), "Customer chunein", sections, ct);
        // A customer not in the 7 shown: the next plain name ("Hina") finds them.
        ctx.TapJob = "order-pick-customer";
    }

    /// <summary>"naya customer": ask for the buyer; the next plain message is saved as the customer and the product picker follows.</summary>
    private async Task StartNewCustomerOrderAsync(Seller seller, SessionContextData ctx, CancellationToken ct)
    {
        ctx.TapJob = "order-customer";
        await ReplyAsync(seller, MessageTemplates.Get("tapflow.customer.ask"), ct);
    }

    /// <summary>Row id of "🔍 Aur products": the next page of the product picker (the same search, if one was typed).</summary>
    internal const string MoreProductsRowId = "tap-more-products";

    /// <summary>
    /// After "order for Sara" with no usual items: the catalog's best sellers as taps; a tap is that product, 1 piece.
    /// 9 a page; "🔍 Aur products" shows the next 9, and a typed name ("lawn") shows only matching products.
    /// </summary>
    private async Task<bool> SendProductPickerAsync(Seller seller, SessionContextData ctx, string prompt, CancellationToken ct, int page = 0, string? filter = null)
    {
        var catalog = await LoadCatalogAsync(seller, ct);
        if (catalog.Count == 0) return false;

        var sold = (await _db.Orders.AsNoTracking().Where(o => o.SellerId == seller.Id)
                .SelectMany(o => o.Items).Where(i => i.ProductId != null)
                .Select(i => new { Id = i.ProductId!.Value, i.Quantity }).ToListAsync(ct))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var matching = catalog
            .Where(c => filter is null || c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => sold.GetValueOrDefault(c.Product.Id)).ThenBy(c => c.Product.Id)
            .Select(c => c.Label).ToList();
        if (matching.Count == 0) return false;
        if (page * PickerProducts >= matching.Count) page = 0; // past the last page: start again
        var labels = matching.Skip(page * PickerProducts).Take(PickerProducts).ToList();
        // List rows hold 24 characters (buttons 20), so names are cut later here.
        var picks = BuildSuggestions(catalog, labels, ListRowTitleMax);
        if (picks.Count == 0) return false;

        ctx.ProductSuggestions = picks;
        ctx.TapCart ??= new List<TapCartItem>();
        ctx.TapPickerPage = page + 1;
        ctx.TapPickerFilter = filter;
        var rows = picks.Select(p => new MenuRow(p.Title, p.Title)).ToList();
        var more = matching.Count > (page + 1) * PickerProducts;
        if (more) rows.Add(new MenuRow(MoreProductsRowId, "🔍 Aur products"));
        var body = prompt + "\n\n" + MessageTemplates.Get("tapflow.order.product", catalog.First(c => c.Label == picks[0].Label).Product.Name);
        if (catalog.Count > PickerProducts) body += "\n" + MessageTemplates.Get("tapflow.order.search");
        await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, body, "Product chunein", new[] { new MenuSection("Product", rows.ToArray()) }, ct);
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex SearchWords = new(@"^[\p{L}][\p{L} .'’-]{1,29}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The delivery / discount taps under the draft of a tap-by-tap order (the draft's own buttons are Save / Edit / Cancel).</summary>
    private async Task SendOrderExtrasAsync(Seller seller, CancellationToken ct)
    {
        var charge = seller.DefaultDeliveryCharge > 0 ? seller.DefaultDeliveryCharge : 200m;
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("tapflow.order.extras"),
            new[] { $"🚚 Delivery {charge.ToString("0.##", CultureInfo.InvariantCulture)}", "🆓 Free delivery", "🏷️ Discount 10%" }, ct);
    }

    // The cart's buttons; the engine matches on these labels, so they stay in code.
    internal const string CartOneMore = "➕ 1 aur";
    internal const string CartAnotherProduct = "🛍️ Aur product";
    internal const string CartDone = "✅ Order banayein";
    private static readonly string[] CartButtons = { CartOneMore, CartAnotherProduct, CartDone };

    /// <summary>
    /// A tap-by-tap order in progress: a tapped product goes in the cart (1 piece), "➕ 1 aur" or a typed number sets how many,
    /// "🛍️ Aur product" shows the products again, "✅ Order banayein" turns the cart into the usual order draft (confirm, Save).
    /// Anything else leaves the cart and runs as usual. Returns true when handled here.
    /// </summary>
    private async Task<bool> TryTapCartAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.TapCart is not { } cart || ctx.OrderForCustomer is not { } target || session.State != ConversationState.Idle) return false;
        var text = message.Trim();
        target.At = DateTime.UtcNow; // each tap keeps the request open

        if (ctx.ProductSuggestions?.FirstOrDefault(p => p.Title == text) is { } tapped)
        {
            var label = tapped.Label.Replace(",", "");
            if (cart.FirstOrDefault(i => i.Label == label) is { } same) same.Qty++;
            else cart.Add(new TapCartItem { Label = label });
            MoveToEnd(cart, label); // "➕ 1 aur" and a typed number act on the product tapped last
            await SendCartAsync(seller, target.Name, cart, ct);
            return true;
        }
        if (cart.Count > 0 && text == CartOneMore)
        {
            cart[^1].Qty++;
            await SendCartAsync(seller, target.Name, cart, ct);
            return true;
        }
        if (cart.Count > 0 && int.TryParse(text, out var qty) && qty is >= 1 and <= 999)
        {
            cart[^1].Qty = qty;
            await SendCartAsync(seller, target.Name, cart, ct);
            return true;
        }
        if (text == CartAnotherProduct)
        {
            await SendProductPickerAsync(seller, ctx, MessageTemplates.Get("tapflow.cart.more", target.Name), ct);
            return true;
        }
        if (text == MoreProductsRowId)
        {
            await SendProductPickerAsync(seller, ctx, MessageTemplates.Get("tapflow.cart.more", target.Name), ct, ctx.TapPickerPage, ctx.TapPickerFilter);
            return true;
        }
        if (cart.Count > 0 && text == CartDone)
        {
            var items = string.Join(", ", cart.Select(i => $"{i.Qty}x {i.Label}"));
            ctx.TapCart = null;
            if (await TryHandleOrderForCustomerItemsAsync(seller, session, ctx, items, ct))
            {
                if (session.State == ConversationState.AwaitingOrderConfirmation) await SendOrderExtrasAsync(seller, ct);
                return true;
            }
            await ReplyAsync(seller, MessageTemplates.Get("tapflow.cart.failed", items), ct);
            return true;
        }
        // A typed word ("lawn") while picking: the products whose name has it. Items with a number ("kurta 2") still go on as typed items.
        if (SearchWords.IsMatch(text) && CommandParser.TryParse(text) is null && !CommandParser.IsAffirmative(text) && !CommandParser.IsNegative(text))
        {
            if (!await SendProductPickerAsync(seller, ctx, MessageTemplates.Get("tapflow.cart.more", target.Name), ct, 0, text))
                await ReplyAsync(seller, MessageTemplates.Get("tapflow.order.nomatch", text), ct);
            return true;
        }

        // Not a cart step: the cart is dropped and the message runs as usual (typed items still go to this customer's order).
        ctx.TapCart = null;
        return false;
    }

    private static void MoveToEnd(List<TapCartItem> cart, string label)
    {
        var item = cart.First(i => i.Label == label);
        cart.Remove(item);
        cart.Add(item);
    }

    private async Task SendCartAsync(Seller seller, string customer, List<TapCartItem> cart, CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync(seller, ct);
        var lines = cart.Select(i =>
        {
            var price = catalog.FirstOrDefault(c => c.Label.Replace(",", "") == i.Label)?.Product.Price;
            return price is { } p ? $"• {i.Qty}x {i.Label} — {Formatters.Money(p * i.Qty)}" : $"• {i.Qty}x {i.Label}";
        });
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            MessageTemplates.Get("tapflow.cart", customer, string.Join("\n", lines), cart[^1].Label), CartButtons, ct);
    }

    /// <summary>"Paisa lena hai": after the unpaid list, each order on it as a tap that marks it paid (its list number, as "mark 2 paid" would).</summary>
    private async Task SendMarkPaidPickerAsync(Seller seller, IReadOnlyList<Order> page, int firstNumber, CancellationToken ct)
    {
        var rows = page.Take(PickerUnpaid).Select((o, i) =>
        {
            var money = " " + Formatters.Money(OrderMoney.Balance(o));
            var name = ShortenChoice("✅ " + (o.Customer?.Name ?? "#" + o.Id.ToString(CultureInfo.InvariantCulture)), ListRowTitleMax - money.Length);
            return new MenuRow($"mark {firstNumber + i} paid", name + money);
        }).ToArray();
        if (rows.Length == 0) return;
        await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("tapflow.unpaid.pick"), "Paid karein",
            new[] { new MenuSection("Paisa mil gaya", rows) }, ct);
    }

    /// <summary>
    /// "Hina ne 1000 diye": the same step as the AI's payment action (her newest open order, YES first, the balance shown), with no AI call.
    /// No such buyer or nothing owed: said so, as for "Hina ka paisa aa gaya".
    /// </summary>
    private async Task HandleCustomerPartPaymentAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var amount = cmd.Amount!.Value;
        var said = $"{cmd.Text} ne {amount.ToString("0.##", CultureInfo.InvariantCulture)} diye";
        var action = new Ai.TurnAction { Action = Ai.TurnAction.RecordPayment, CustomerName = cmd.Text, Amount = amount };
        if (await ModelPaymentOrderAsync(seller, action, said, ct) is not null
            && await RunTurnActionAsync(seller, session, ctx, action, await LoadCatalogAsync(seller, ct), said, ct, fromScreenshot: false))
            return;
        await HandleCustomerPaidAsync(seller, ctx, cmd.Text!, ct);
    }

    /// <summary>"Hina ka paisa aa gaya": her unpaid orders, each a tap that marks it paid. The sentence alone marks nothing.</summary>
    private async Task HandleCustomerPaidAsync(Seller seller, SessionContextData ctx, string name, CancellationToken ct)
    {
        var customers = await _db.Customers.AsNoTracking().Where(c => c.SellerId == seller.Id && c.DeletedAt == null).ToListAsync(ct);
        var matched = customers.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0) matched = customers.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("tapflow.paid.nocustomer", name), ct);
            return;
        }

        var ids = matched.Select(c => c.Id).ToList();
        var orders = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && ids.Contains(o.CustomerId) &&o.PaymentStatus == PaymentStatus.Unpaid
                        && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .OrderBy(o => o.CreatedAt).Take(PickerUnpaid).ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("tapflow.paid.nothing", matched[0].Name), ct);
            return;
        }

        // The taps say "mark 1 paid" ...: their numbers point into this list.
        ctx.LastListOrderIds = orders.Select(o => o.Id).ToList();
        await ReplyAsync(seller, MessageTemplates.Get("tapflow.paid.owes", matched[0].Name, orders.Count, Formatters.Money(orders.Sum(OrderMoney.Balance))), ct);
        await SendMarkPaidPickerAsync(seller, orders, 1, ct);
    }

    /// <summary>
    /// After "Add product": every line of the message is a product, with or without the dash ("lawn suit 4500", "Kurta - 2200, size S/M/L").
    /// All lines products: they go on as product lines and the chain stays on (after Save the next message is read the same way).
    /// Anything else ends the chain and the message runs as usual.
    /// </summary>
    private static string ProductTapAnswer(ConversationSession session, SessionContextData ctx, string message)
    {
        ctx.ProductChain = false;
        if (session.State != ConversationState.Idle) return message;
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return message;
        var asProducts = new List<string>();
        foreach (var line in lines)
        {
            if (CommandParser.TryParseProductLine(line, out ProductLine? _)) asProducts.Add(line);
            else if (CommandParser.TryParseSpacedProductLine(line, out var spaced) && spaced is not null)
                asProducts.Add($"{spaced.Name} - {spaced.Price.ToString("0.##", CultureInfo.InvariantCulture)}");
            else return message;
        }
        ctx.ProductChain = true;
        return string.Join("\n", asProducts);
    }

    /// <summary>
    /// The one message after a job was tapped ("Naya customer", "Add product", "Kharcha likhna", "Tankhwah dena"): a plain answer is that entry.
    /// Returns true when it was handled here; otherwise <paramref name="message"/> may come back rewritten as the command it means, to run as usual.
    /// A real command, a yes / no, or text that is not such an entry is left alone.
    /// </summary>
    private async Task<(bool Handled, string Message)> TryTapJobAnswerAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.TapJob is not { } job) return (false, message);
        ctx.TapJob = null;
        if (job == "product") return (false, ProductTapAnswer(session, ctx, message));
        if (session.State != ConversationState.Idle || CommandParser.TryParse(message) is not null
            || CommandParser.IsAffirmative(message) || CommandParser.IsNegative(message))
            return (false, message);

        var text = message.Trim();
        switch (job)
        {
            case "order-pick-customer":
                // Only a plain name: a whole order typed after "Naya order" ("Hina, 2 suit, 0300...") runs as usual.
                if (!SearchWords.IsMatch(text) || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3) return (false, message);
                await HandleOrderForCustomerAsync(seller, session, ctx, text, ct);
                return (true, message);

            case "order-customer":
                if (CommandParser.TryParse("customer add " + text) is not { Kind: CommandKind.CustomerAdd } add) return (false, message);
                await ReplyAsync(seller, await SaveNewCustomerAsync(seller, new Ai.AiNewCustomer { Name = add.Text!, Phone = add.Text2, Address = add.Text3, City = add.Text4 }, ct), ct);
                await HandleOrderForCustomerAsync(seller, session, ctx, add.Text!, ct);
                return (true, message);

            case "expense":
            case "wage":
            {
                var amount = TapAmount.Match(text);
                if (!amount.Success) return (false, message);
                var number = amount.Value.Replace(",", "");
                var words = (text[..amount.Index] + " " + text[(amount.Index + amount.Length)..]).Trim();
                words = Regex.Replace(words, @"\s+", " ");
                if (words.Length == 0) return (false, message);
                var rewritten = job == "expense" ? $"expense {number} {words}" : $"{words} ko {number} tankhwah di";
                var meant = CommandParser.TryParse(rewritten)?.Kind;
                return meant == (job == "expense" ? CommandKind.Expense : CommandKind.Wage) ? (false, rewritten) : (false, message);
            }
        }
        return (false, message);
    }
}
