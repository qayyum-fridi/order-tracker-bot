using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Minimum Pakistani tax details for receipts: the seller's NTN / STRN, an optional sales-tax rate that is *included* in their prices
// (order totals never change; the invoice splits the tax out), and a per-order "tax withheld" note for what a courier or payment
// gateway kept back from the payout. Withholding is the seller's own record: it never appears on a buyer's receipt.
public partial class ConversationEngine
{
    private static readonly string TaxDisclaimer = MessageTemplates.Get("tax.015");

    private async Task HandleTaxSettingsAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var kind = cmd.Text ?? "show";
        var value = cmd.Text2;
        var clearing = value is not null && !char.IsDigit(value[0]);

        switch (kind)
        {
            case "ntn" or "strn" when value is null:
                var current = kind == "ntn" ? seller.Ntn : seller.Strn;
                await ReplyAsync(seller, current is null
                    ? MessageTemplates.Get("tax.009", kind.ToUpperInvariant(), (kind == "ntn" ? "ntn 1234567-8" : "strn 17-00-8888-001-37"))
                    : MessageTemplates.Get("tax.010", kind.ToUpperInvariant(), current, kind), ct);
                return;

            case "ntn" or "strn" when clearing:
                if (kind == "ntn") seller.Ntn = null; else seller.Strn = null;
                await ReplyAsync(seller, MessageTemplates.Get("tax.001", kind.ToUpperInvariant()), ct);
                return;

            case "ntn" or "strn":
                var digits = value!.Count(char.IsDigit);
                if (digits is < 7 or > 15)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("tax.002", kind.ToUpperInvariant(), (kind == "ntn" ? "jaise ntn 1234567-8" : "jaise strn 17-00-8888-001-37")), ct);
                    return;
                }
                var cleaned = string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (kind == "ntn") seller.Ntn = cleaned; else seller.Strn = cleaned;
                await ReplyAsync(seller, MessageTemplates.Get("tax.003", kind.ToUpperInvariant(), cleaned), ct);
                return;

            case "rate" when value is null:
                await ReplyAsync(seller, seller.SalesTaxRate > 0
                    ? MessageTemplates.Get("tax.011", SalesTax.Percent(seller.SalesTaxRate))
                    : MessageTemplates.Get("tax.012"), ct);
                return;

            case "rate":
                var rate = value == "set" ? cmd.Amount ?? 0 : 0;
                if (rate > 30)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("tax.004"), ct);
                    return;
                }
                seller.SalesTaxRate = rate;
                await ReplyAsync(seller, rate > 0
                    ? MessageTemplates.Get("tax.013", SalesTax.Percent(rate), (string.IsNullOrWhiteSpace(seller.Strn) ? "⚠️ Tax invoice par STRN bhi hona chahiye: strn 17-00-8888-001-37\n" : ""), TaxDisclaimer)
                    : MessageTemplates.Get("tax.014"), ct);
                return;

            default:
                await ReplyAsync(seller,
                    MessageTemplates.Get("tax.005", seller.Ntn ?? "set nahi", seller.Strn ?? "set nahi", (seller.SalesTaxRate > 0 ? SalesTax.Percent(seller.SalesTaxRate) + "% (prices mein shamil)" : "band")), ct);
                return;
        }
    }

    private async Task HandleOrderTaxWithheldAsync(Seller seller, SessionContextData ctx, int number, decimal amount, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, number);
        var order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("tax.006", number), ct);
            return;
        }
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await ReplyAsync(seller, MessageTemplates.Get("tax.007", order.Id, Formatters.Status(order.Status)), ct);
            return;
        }
        if (amount > order.Total)
        {
            await ReplyAsync(seller, MessageTemplates.Get("tax.008", Formatters.Money(order.Total)), ct);
            return;
        }

        order.TaxWithheld = amount;
        await ReplyAsync(seller,
            (amount > 0
                ? $"✅ Order #{order.Id} ({order.Customer?.Name}): tax withheld {Formatters.Money(amount)} note kar liya — aap ko {Formatters.Money(SalesTax.NetOfWithheld(order))} milenge.\n" +
                  "Ye sirf aapke record/reports ke liye hai, customer ki receipt par nahi aata."
                : $"✅ Order #{order.Id}: tax withheld hata diya.") +
            (fromList ? $"\nℹ️ \"{number}\" aapki last list ka number tha." : ""), ct);
    }
}
