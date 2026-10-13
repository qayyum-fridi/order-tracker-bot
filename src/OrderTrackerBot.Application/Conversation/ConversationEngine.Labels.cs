using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// "label 12" (a parcel label for the courier), "business label" (the shop's own sticker), "label size 4x6|a6|50x30".
// Like receipts these are PDFs sent to the seller's own chat; the seller prints them (a 4x6 thermal label printer, or an A6 sheet).
public partial class ConversationEngine
{
    /// <summary>"4x6" | "A6" | "50x30" for the spellings sellers use; null when it is none of them.</summary>
    public static string? NormalizeLabelSize(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("*", "x").Replace("×", "x").Replace("mm", "").Replace("inch", "");
        return t switch
        {
            "4x6" or "100x150" or "standard" or "courier" => "4x6",
            "a6" => "A6",
            "50x30" or "30x50" or "sticker" or "small" => "50x30",
            _ => null
        };
    }

    private async Task HandleLabelSizeAsync(Seller seller, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await ReplyAsync(seller,
                $"🏷️ Label size abhi: {NormalizeLabelSize(seller.LabelSize) ?? "4x6"}\n" +
                "• 4x6 inch: courier ka aam label (thermal label printer)\n• A6: chhota sheet\n• 50x30: chhota sticker (sirf business label)\n" +
                "Badalne ke liye: label size 4x6 / label size a6 / label size 50x30", ct);
            return;
        }
        var size = NormalizeLabelSize(text);
        if (size is null)
        {
            await ReplyAsync(seller, "Maaf kijiye, size samajh nahi aaya. Likhein: label size 4x6, label size a6 ya label size 50x30.", ct);
            return;
        }
        seller.LabelSize = size == "4x6" ? null : size;
        await ReplyAsync(seller, $"✅ Label ab {size} mein banega. Try karein: \"label\" ya \"business label\"." +
                                 (size == "50x30" ? "\nℹ️ 50x30 par parcel ka address nahi aata: shipping label 4x6 mein hi banega." : ""), ct);
    }

    private async Task HandleShippingLabelAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        if (_receiptPdf is null)
        {
            await ReplyAsync(seller, "PDF label abhi available nahi hai.", ct);
            return;
        }
        if (cmd.Text is not null && System.Text.RegularExpressions.Regex.IsMatch(cmd.Text, @"^(?:printer|help|guide)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            await HandlePrinterHelpAsync(seller, ct);
            return;
        }

        // "label" alone could be a parcel label or the shop's own sticker: ask, with the likely answers as taps.
        if (cmd.Number is null && string.IsNullOrWhiteSpace(cmd.Text))
        {
            var latest = await _db.Orders.AsNoTracking().Include(o => o.Customer)
                .Where(o => o.SellerId == seller.Id).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct);
            var options = new List<string>();
            if (latest is not null) options.Add($"label {latest.Id}");
            options.Add("business label");
            options.Add("label size");
            await AskClarificationAsync(seller, session, ctx,
                latest is null
                    ? "🏷️ Kaunsa label chahiye? (Abhi koi order nahi, is liye parcel label nahi ban sakta.)"
                    : $"🏷️ Kaunsa label chahiye?\n• label {latest.Id}: Order #{latest.Id} ({latest.Customer?.Name}) ka parcel label\n• business label: aapki dukan ka sticker\n• label size: size dekhein/badlein",
                options, ct);
            return;
        }

        var (order, fromList) = await FindReceiptOrderAsync(seller, ctx, new ParsedCommand { Kind = CommandKind.Receipt, Number = cmd.Number, Text = cmd.Text }, ct);
        if (order is null)
        {
            await ReplyAsync(seller, cmd.Number is not null ? $"Order #{cmd.Number} nahi mila. Apne orders dekhne ke liye \"orders today\" likhein."
                : $"{cmd.Text} ka koi order nahi mila.", ct);
            return;
        }
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await ReplyAsync(seller, $"Order #{order.Id} {Formatters.Status(order.Status)} hai — iska parcel label nahi banta.", ct);
            return;
        }

        var address = string.Join(", ", new[] { order.Customer?.Address, order.Customer?.City }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var isCod = order.PaymentMethod == OrderPaymentMethod.Cod && order.PaymentStatus != PaymentStatus.Paid;
        var pdf = _receiptPdf.GenerateShippingLabel(new ShippingLabelData(
            order.Id, seller.BusinessName ?? "—", seller.WhatsAppPhoneNumber, seller.City,
            order.Customer?.Name ?? "Customer", order.Customer?.Phone, address,
            string.Join(", ", order.Items.Select(i => $"{i.Quantity}x {i.ProductNameSnapshot}")),
            isCod, OrderMoney.Balance(order), order.TrackingCourier, order.TrackingNumber, order.Notes,
            NormalizeLabelSize(seller.LabelSize) ?? "4x6"));

        var sent = await _sender.SendDocumentAsync(seller.WhatsAppPhoneNumber, pdf, $"Label-{order.Id}.pdf", "application/pdf",
            $"🏷️ Parcel label — Order #{order.Id}, {order.Customer?.Name}" + (isCod ? $" · COD {Formatters.Money(OrderMoney.Balance(order))}" : ""), ct);
        if (!sent)
        {
            await ReplyAsync(seller, "⚠️ Label bhej nahi saka — thori der baad dobara try karein.", ct);
            return;
        }

        // What usually comes next, and what is probably missing, so the seller does not have to remember it.
        var next = new List<string>();
        if (string.IsNullOrWhiteSpace(address)) next.Add($"⚠️ Address nahi hai: \"edit order {order.Id}\" phir \"address ...\" likhein, aur label dobara banayein.");
        if (string.IsNullOrWhiteSpace(order.Customer?.Phone)) next.Add($"⚠️ Customer ka phone nahi hai: \"edit order {order.Id}\" phir \"phone ...\".");
        if (order.Status == OrderStatus.Pending) next.Add($"Parcel bhejne ke baad: \"mark {order.Id} shipped\"");
        if (string.IsNullOrWhiteSpace(order.TrackingNumber)) next.Add($"Tracking mile to: \"add tracking: {order.Id}, <number>\"");
        await ReplyAsync(seller,
            "✅ Label ready — file par tap karke print karein (\"printer\" likhein to tareeqa bataun)." +
            (next.Count == 0 ? "" : "\n\n" + string.Join("\n", next)) +
            (fromList ? $"\nℹ️ \"{cmd.Number}\" aapki last list ka number tha (Order #{order.Id})." : ""), ct);
    }

    private async Task HandleBusinessLabelAsync(Seller seller, CancellationToken ct)
    {
        if (_receiptPdf is null)
        {
            await ReplyAsync(seller, "PDF label abhi available nahi hai.", ct);
            return;
        }
        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        var pdf = _receiptPdf.GenerateBusinessLabel(new BusinessLabelData(
            seller.BusinessName ?? "My Shop", seller.WhatsAppPhoneNumber, seller.City, seller.InstagramHandle, seller.ReceiptFooter,
            branding?.Logo, NormalizeLabelSize(seller.LabelSize) ?? "4x6"));

        var sent = await _sender.SendDocumentAsync(seller.WhatsAppPhoneNumber, pdf, "Business-label.pdf", "application/pdf",
            $"🏷️ {seller.BusinessName} ka sticker", ct);
        if (!sent)
        {
            await ReplyAsync(seller, "⚠️ Label bhej nahi saka — thori der baad dobara try karein.", ct);
            return;
        }
        await ReplyAsync(seller,
            "✅ Business label ready — print karke parcel/bag par lagayein." +
            (branding?.Logo is null ? "\n💡 Apna logo lagane ke liye \"logo\" likhein." : "") +
            "\nSize badalne ke liye: label size 50x30 / a6 / 4x6", ct);
    }
}
