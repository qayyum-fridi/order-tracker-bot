using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Minimum Pakistani tax details for receipts: the seller's NTN / STRN, an optional sales-tax rate that is *included* in their prices
// (order totals never change; the invoice splits the tax out), and a per-order "tax withheld" note for what a courier or payment
// gateway kept back from the payout. Withholding is the seller's own record: it never appears on a buyer's receipt.
public partial class ConversationEngine
{
    private const string TaxDisclaimer = "ℹ️ Sales tax sirf sales-tax registered sellers lagate hain, aur bari sale par buyer ka CNIC/NTN bhi invoice par chahiye ho sakta hai — apne accountant se confirm karein.";

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
                    ? $"{kind.ToUpperInvariant()} abhi set nahi. Set karne ke liye likhein: {(kind == "ntn" ? "ntn 1234567-8" : "strn 17-00-8888-001-37")}"
                    : $"{kind.ToUpperInvariant()}: {current}\nBadalne ke liye naya number likhein, hatane ke liye \"{kind} off\".", ct);
                return;

            case "ntn" or "strn" when clearing:
                if (kind == "ntn") seller.Ntn = null; else seller.Strn = null;
                await ReplyAsync(seller, $"✅ {kind.ToUpperInvariant()} hata diya — ab receipt par nahi aayega.", ct);
                return;

            case "ntn" or "strn":
                var digits = value!.Count(char.IsDigit);
                if (digits is < 7 or > 15)
                {
                    await ReplyAsync(seller, $"{kind.ToUpperInvariant()} number theek nahi lagta — {(kind == "ntn" ? "jaise ntn 1234567-8" : "jaise strn 17-00-8888-001-37")} likhein.", ct);
                    return;
                }
                var cleaned = string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (kind == "ntn") seller.Ntn = cleaned; else seller.Strn = cleaned;
                await ReplyAsync(seller, $"✅ {kind.ToUpperInvariant()} {cleaned} save ho gaya — ab har receipt ke upar likha aayega.", ct);
                return;

            case "rate" when value is null:
                await ReplyAsync(seller, seller.SalesTaxRate > 0
                    ? $"🧾 Sales tax: {SalesTax.Percent(seller.SalesTaxRate)}% (aapki prices mein shamil). Badalne ke liye \"sales tax 18\", band karne ke liye \"sales tax off\"."
                    : "🧾 Sales tax abhi band hai. Registered hain to likhein: sales tax 18", ct);
                return;

            case "rate":
                var rate = value == "set" ? cmd.Amount ?? 0 : 0;
                if (rate > 30)
                {
                    await ReplyAsync(seller, "Sales tax 30% se zyada nahi ho sakta — jaise \"sales tax 18\" likhein.", ct);
                    return;
                }
                seller.SalesTaxRate = rate;
                await ReplyAsync(seller, rate > 0
                    ? $"✅ Sales tax {SalesTax.Percent(rate)}% set — maana jayega ke aapki prices mein pehle se shamil hai, is liye order ka total nahi badlega. " +
                      "Naye orders ki receipt \"TAX INVOICE\" hogi jis par tax alag likha aayega; purane orders par asar nahi.\n" +
                      (string.IsNullOrWhiteSpace(seller.Strn) ? "⚠️ Tax invoice par STRN bhi hona chahiye: strn 17-00-8888-001-37\n" : "") + TaxDisclaimer
                    : "✅ Sales tax band — naye orders ki receipt phir se simple \"RECEIPT\" hogi.", ct);
                return;

            default:
                await ReplyAsync(seller,
                    "🧾 Tax setup\n" +
                    $"NTN: {seller.Ntn ?? "set nahi"}\n" +
                    $"STRN: {seller.Strn ?? "set nahi"}\n" +
                    $"Sales tax: {(seller.SalesTaxRate > 0 ? SalesTax.Percent(seller.SalesTaxRate) + "% (prices mein shamil)" : "band")}\n\n" +
                    "Badalne ke liye: ntn 1234567-8 · strn 17-00-8888-001-37 · sales tax 18 · sales tax off\n" +
                    "Courier/gateway ne tax kaata ho to: order 12 withheld 120", ct);
                return;
        }
    }

    private async Task HandleOrderTaxWithheldAsync(Seller seller, SessionContextData ctx, int number, decimal amount, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, number);
        var order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, $"Order #{number} nahi mila.", ct);
            return;
        }
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await ReplyAsync(seller, $"Order #{order.Id} {Formatters.Status(order.Status)} hai — tax withheld note nahi ho sakta.", ct);
            return;
        }
        if (amount > order.Total)
        {
            await ReplyAsync(seller, $"Withheld raqam order total ({Formatters.Money(order.Total)}) se zyada nahi ho sakti.", ct);
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
