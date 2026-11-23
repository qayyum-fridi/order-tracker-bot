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
    private async Task SendNewOrderPickerAsync(Seller seller, CancellationToken ct)
    {
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
    }

    /// <summary>"naya customer": ask for the buyer; the next plain message is saved as the customer and the product picker follows.</summary>
    private async Task StartNewCustomerOrderAsync(Seller seller, SessionContextData ctx, CancellationToken ct)
    {
        ctx.TapJob = "order-customer";
        await ReplyAsync(seller, MessageTemplates.Get("tapflow.customer.ask"), ct);
    }

    /// <summary>After "order for Sara" with no usual items: the catalog's best sellers as taps; a tap is that product, 1 piece.</summary>
    private async Task<bool> SendProductPickerAsync(Seller seller, SessionContextData ctx, string prompt, CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync(seller, ct);
        if (catalog.Count == 0) return false;

        var sold = (await _db.Orders.AsNoTracking().Where(o => o.SellerId == seller.Id)
                .SelectMany(o => o.Items).Where(i => i.ProductId != null)
                .Select(i => new { Id = i.ProductId!.Value, i.Quantity }).ToListAsync(ct))
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var labels = catalog
            .OrderByDescending(c => sold.GetValueOrDefault(c.Product.Id)).ThenBy(c => c.Product.Id)
            .Select(c => c.Label).Take(PickerProducts).ToList();
        var picks = BuildSuggestions(catalog, labels);
        if (picks.Count == 0) return false;

        ctx.ProductSuggestions = picks;
        var rows = picks.Select(p => new MenuRow(p.Title, p.Title)).ToArray();
        var body = prompt + "\n\n" + MessageTemplates.Get("tapflow.order.product", catalog.First(c => c.Label == picks[0].Label).Product.Name);
        await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, body, "Product chunein", new[] { new MenuSection("Product", rows) }, ct);
        return true;
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
    /// The one message after a job was tapped ("Naya customer", "Add product", "Kharcha likhna", "Tankhwah dena"): a plain answer is that entry.
    /// Returns true when it was handled here; otherwise <paramref name="message"/> may come back rewritten as the command it means, to run as usual.
    /// A real command, a yes / no, or text that is not such an entry is left alone.
    /// </summary>
    private async Task<(bool Handled, string Message)> TryTapJobAnswerAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.TapJob is not { } job) return (false, message);
        ctx.TapJob = null;
        if (session.State != ConversationState.Idle || CommandParser.TryParse(message) is not null
            || CommandParser.IsAffirmative(message) || CommandParser.IsNegative(message))
            return (false, message);

        var text = message.Trim();
        switch (job)
        {
            case "order-customer":
                if (CommandParser.TryParse("customer add " + text) is not { Kind: CommandKind.CustomerAdd } add) return (false, message);
                await ReplyAsync(seller, await SaveNewCustomerAsync(seller, new Ai.AiNewCustomer { Name = add.Text!, Phone = add.Text2, Address = add.Text3, City = add.Text4 }, ct), ct);
                await HandleOrderForCustomerAsync(seller, session, ctx, add.Text!, ct);
                return (true, message);

            case "product":
                return CommandParser.TryParseSpacedProductLine(text, out var line) && line is not null
                    ? (false, $"{line.Name} - {line.Price.ToString("0.##", CultureInfo.InvariantCulture)}")
                    : (false, message);

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
