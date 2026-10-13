using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// "receipt" / "receipt 12" / "Ayesha ki receipt": a PDF sent to the seller's own chat, which they forward to the buyer.
// The bot never messages buyers itself (they never opened a conversation with the business number).
// Paper size, footer, order note and email: ConversationEngine.ReceiptSettings.cs.
public partial class ConversationEngine
{
    private async Task HandleReceiptAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        if (_receiptPdf is null)
        {
            await ReplyAsync(seller, "PDF receipt abhi available nahi hai.", ct);
            return;
        }

        var (order, fromList) = await FindReceiptOrderAsync(seller, ctx, cmd, ct);
        if (order is null)
        {
            await ReplyAsync(seller, cmd.Number is not null ? $"Order #{cmd.Number} nahi mila. Apne orders dekhne ke liye \"orders today\" likhein."
                : cmd.Text is not null ? $"{cmd.Text} ka koi order nahi mila." : "Abhi koi order nahi hai.", ct);
            return;
        }

        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        var (pdf, numberIsNew) = await RenderReceiptAsync(seller, order, branding, ct);

        var sent = await _sender.SendDocumentAsync(seller.WhatsAppPhoneNumber, pdf, $"Receipt-{order.Id}.pdf", "application/pdf",
            $"🧾 {(order.SalesTaxRate > 0 ? "Tax invoice" : "Receipt")} No. {order.ReceiptNumber} (Order #{order.Id}) — {order.Customer?.Name}, {Formatters.Money(order.Total)}", ct);
        if (!sent)
        {
            if (numberIsNew) order.ReceiptNumber = null; // never burn a serial number on a receipt the seller didn't get
            await ReplyAsync(seller, "⚠️ Receipt PDF bhej nahi saka — thori der baad dobara try karein.", ct);
            return;
        }

        var suggestBranding = await ShouldSuggestBrandingAsync(seller, branding, ct);
        var chat = ChatLink(order.Customer?.Phone);
        await ReplyAsync(seller,
            "✅ Receipt PDF ready — download ke liye file par tap karein.\n" +
            "Customer ko bhejne ke liye is PDF ko Forward karein" + (chat is null ? "." : $", ya unki chat yahan kholein:\n{chat}") +
            (IsThermalSize(seller.ReceiptSize) ? $"\n\n🖨️ Receipt {seller.ReceiptSize} ke hisab se bani hai. Print karne ka tareeqa: \"printer\" likhein." : "") +
            (suggestBranding ? "\n\n💡 Receipt par apna logo ya banner lagana chahte hain? \"logo\" ya \"banner\" likhein — main bata dunga kaise." : ""), ct);
        if (fromList)
            await ReplyAsync(seller, $"ℹ️ \"{cmd.Number}\" aapki last list ka number tha (Order #{order.Id}).", ct);
    }

    /// <summary>The order a receipt command points at: a number (or a number from the last list), a customer's latest order, or the newest order.</summary>
    private async Task<(Order? Order, bool FromList)> FindReceiptOrderAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        if (cmd.Number is { } number)
        {
            var (orderId, listed) = ResolveListNumber(ctx, number);
            var byNumber = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
            return (byNumber, listed);
        }
        if (!string.IsNullOrWhiteSpace(cmd.Text))
            return (await FindLatestOrderByCustomerNameAsync(seller, cmd.Text, ct), false);

        var latest = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        return (latest, false);
    }

    /// <summary>
    /// The receipt PDF for one order, in the seller's paper size, with their footer and the order's note. Invoices are serially numbered per seller; the number is
    /// given the first time one is issued and kept on re-sends. <c>NumberIsNew</c> tells the caller to give the number back when the receipt could not be delivered.
    /// </summary>
    private async Task<(byte[] Pdf, bool NumberIsNew)> RenderReceiptAsync(Seller seller, Order order, SellerBranding? branding, CancellationToken ct)
    {
        var payTo = order.PaymentStatus == PaymentStatus.Paid || order.PaymentMethod == OrderPaymentMethod.Cod
            ? new List<string>()
            : (await _db.PaymentMethods.Where(p => p.SellerId == seller.Id).OrderBy(p => p.Id).ToListAsync(ct))
                .Select(p => $"{p.Type}: {p.AccountNumberOrId}").ToList();

        var tz = SellerClock.Resolve(seller.TimeZoneId);
        var numberIsNew = order.ReceiptNumber is null;
        if (numberIsNew)
            order.ReceiptNumber = (await _db.Orders.Where(o => o.SellerId == seller.Id).MaxAsync(o => o.ReceiptNumber, ct) ?? 0) + 1;

        var pdf = _receiptPdf!.Generate(new ReceiptData(
            order.Id,
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(order.CreatedAt, DateTimeKind.Utc), tz),
            seller.BusinessName ?? "Order Receipt",
            seller.WhatsAppPhoneNumber, seller.City, seller.InstagramHandle,
            order.Customer?.Name ?? "Customer", order.Customer?.Phone,
            string.Join(", ", new[] { order.Customer?.Address, order.Customer?.City }.Where(s => !string.IsNullOrWhiteSpace(s))),
            order.Items.Select(i => new ReceiptLine(i.ProductNameSnapshot, i.Quantity, i.UnitPrice, i.LineTotal)).ToList(),
            order.Subtotal, order.DiscountAmount, order.DiscountCode, order.Total,
            order.PaymentMethod switch { OrderPaymentMethod.Cod => "Cash on delivery", OrderPaymentMethod.Manual => "Bank / wallet transfer", OrderPaymentMethod.Gateway => "Online payment", _ => "Not specified" },
            order.PaymentStatus == PaymentStatus.Paid, Formatters.Status(order.Status),
            order.TrackingCourier, order.TrackingNumber, payTo, Logo: branding?.Logo, Banner: branding?.Banner, DeliveryCharge: order.DeliveryCharge, AmountPaid: OrderMoney.Received(order),
            ReceiptNumber: order.ReceiptNumber, Ntn: seller.Ntn, Strn: seller.Strn,
            SalesTaxRate: order.SalesTaxRate, SalesTaxAmount: SalesTax.Amount(order),
            PaperSize: NormalizeReceiptSize(seller.ReceiptSize) ?? "A5", Footer: seller.ReceiptFooter, Notes: order.Notes,
            Header: seller.ReceiptHeader, Contact: seller.ReceiptContact));
        return (pdf, numberIsNew);
    }

    /// <summary>wa.me link from a Pakistani local (03001234567) or international (923001234567) number; null if it isn't one.</summary>
    public static string? ChatLink(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00")) digits = digits[2..];
        if (digits.Length == 11 && digits.StartsWith("03")) digits = "92" + digits[1..];
        return digits.Length is >= 11 and <= 15 ? $"https://wa.me/{digits}" : null;
    }
}
