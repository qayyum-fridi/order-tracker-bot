using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// "receipt" / "receipt 12" / "Ayesha ki receipt": a PDF sent to the seller's own chat, which they forward to the buyer.
// The bot never messages buyers itself (they never opened a conversation with the business number).
public partial class ConversationEngine
{
    private async Task HandleReceiptAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        if (_receiptPdf is null)
        {
            await ReplyAsync(seller, "PDF receipt abhi available nahi hai.", ct);
            return;
        }

        Order? order;
        var fromList = false;
        if (cmd.Number is { } number)
        {
            var (orderId, listed) = ResolveListNumber(ctx, number);
            fromList = listed;
            order = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(cmd.Text))
        {
            order = await FindLatestOrderByCustomerNameAsync(seller, cmd.Text, ct);
        }
        else
        {
            order = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
                .Where(o => o.SellerId == seller.Id)
                .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        }

        if (order is null)
        {
            await ReplyAsync(seller, cmd.Number is not null ? $"Order #{cmd.Number} nahi mila."
                : cmd.Text is not null ? $"{cmd.Text} ka koi order nahi mila." : "Abhi koi order nahi hai.", ct);
            return;
        }

        var payTo = order.PaymentStatus == PaymentStatus.Paid || order.PaymentMethod == OrderPaymentMethod.Cod
            ? new List<string>()
            : (await _db.PaymentMethods.Where(p => p.SellerId == seller.Id).OrderBy(p => p.Id).ToListAsync(ct))
                .Select(p => $"{p.Type}: {p.AccountNumberOrId}").ToList();

        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        var tz = SellerClock.Resolve(seller.TimeZoneId);
        var pdf = _receiptPdf.Generate(new ReceiptData(
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
            order.TrackingCourier, order.TrackingNumber, payTo, Logo: branding?.Logo, Banner: branding?.Banner));

        var sent = await _sender.SendDocumentAsync(seller.WhatsAppPhoneNumber, pdf, $"Receipt-{order.Id}.pdf", "application/pdf",
            $"🧾 Receipt #{order.Id} — {order.Customer?.Name}, {Formatters.Money(order.Total)}", ct);
        if (!sent)
        {
            await ReplyAsync(seller, "⚠️ Receipt PDF bhej nahi saka — thori der baad dobara try karein.", ct);
            return;
        }

        var suggestBranding = await ShouldSuggestBrandingAsync(seller, branding, ct);
        var chat = ChatLink(order.Customer?.Phone);
        await ReplyAsync(seller,
            "✅ Receipt PDF ready — download ke liye file par tap karein.\n" +
            "Customer ko bhejne ke liye is PDF ko Forward karein" + (chat is null ? "." : $", ya unki chat yahan kholein:\n{chat}") +
            (suggestBranding ? "\n\n💡 Receipt par apna logo ya banner lagana chahte hain? \"logo\" ya \"banner\" likhein — main bata dunga kaise." : ""), ct);
        if (fromList)
            await ReplyAsync(seller, $"ℹ️ \"{cmd.Number}\" aapki last list ka number tha (Order #{order.Id}).", ct);
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
