using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Everyday receipt settings for a seller with a thermal (or any) printer, an order note, and receipts by email.
//   receipt settings | receipt size 80mm|58mm|A5|A4 | receipt header <line>|off | receipt contact <line>|off | receipt footer <line>|off | note 12 <text>|off
//   logo / banner: a picture captioned "logo" or "banner" (ConversationEngine.Branding.cs)
//   email ali@shop.com | email off | email receipt [12] [to someone@mail.com] | printer
// The bot never prints by itself: it makes a PDF in the right paper size, and the seller prints it from their phone or computer.
public partial class ConversationEngine
{
    private static readonly Regex EmailAddress = new(@"^[^\s@,;<>]+@[^\s@,;<>]+\.[^\s@,;<>]{2,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int MaxFooterLength = 120;
    private const int MaxHeaderLength = 80;
    private const int MaxContactLength = 150;
    private const int MaxNoteLength = 300;
    private const int EmailsPerHour = 20;
    private static readonly Dictionary<int, Queue<DateTime>> EmailSendTimes = new();

    /// <summary>"A5" | "A4" | "80mm" | "58mm" for the spellings sellers use ("80", "3 inch", "thermal"); null when it is none of them.</summary>
    public static string? NormalizeReceiptSize(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        return t switch
        {
            "a5" => "A5",
            "a4" => "A4",
            "80" or "80mm" or "3inch" or "3in" or "thermal" or "thermal80" => "80mm",
            "58" or "58mm" or "2inch" or "2in" or "thermal58" => "58mm",
            _ => null
        };
    }

    public static bool IsThermalSize(string? size) => NormalizeReceiptSize(size) is "80mm" or "58mm";

    private static bool ClearWord(string? text) =>
        text is not null && Regex.IsMatch(text.Trim(), @"^(?:off|none|hata\s*do|hata|remove|delete|default|clear)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task HandleReceiptSettingsAsync(Seller seller, CancellationToken ct)
    {
        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        await ReplyAsync(seller,
            "🧾 Receipt settings\n" +
            $"Size: {NormalizeReceiptSize(seller.ReceiptSize) ?? "A5"}\n" +
            $"Logo: {(branding?.Logo is not null ? "set ✅" : "abhi nahi")}\n" +
            $"Banner: {(branding?.Banner is not null ? "set ✅" : "abhi nahi")}\n" +
            $"Header: {Or(seller.ReceiptHeader, "nahi (sirf dukan ka naam)")}\n" +
            $"Contact: {Or(seller.ReceiptContact, "nahi")}\n" +
            $"Footer: {Or(seller.ReceiptFooter, "default (Thank you ...)")}\n" +
            $"Email: {seller.Email ?? "set nahi"}{(_email is { IsConfigured: true } ? "" : " (email service abhi server par on nahi)")}\n\n" +
            "Badalne ke liye:\n" +
            "• logo ya banner: tasveer bhejein, caption mein \"logo\" ya \"banner\"  (hatane ke liye: remove logo)\n" +
            "• receipt header Gulberg Branch · Est. 2015  (hatane ke liye: receipt header off)\n" +
            "• receipt contact 0300-1234567 · ali@shop.com  (hatane ke liye: receipt contact off)\n" +
            "• receipt size 80mm  (ya 58mm / A5 / A4)\n" +
            "• receipt footer Shukriya, dobara tashreef layein  (hatane ke liye: receipt footer off)\n" +
            "• note 12 gift wrap  (order ka note, receipt par bhi chhapta hai; hatane ke liye: note 12 off)\n" +
            "• email ali@shop.com, phir: email receipt 12  (email bhejne ke liye; receipt par chhapne ke liye receipt contact)\n" +
            "• printer  (print karne ka tareeqa)", ct);
    }

    private async Task HandleReceiptSizeAsync(Seller seller, string? text, CancellationToken ct)
    {
        var size = NormalizeReceiptSize(text);
        if (size is null)
        {
            await ReplyAsync(seller, "Maaf kijiye, size samajh nahi aaya. Likhein: receipt size 80mm, receipt size 58mm, receipt size A5 ya receipt size A4.", ct);
            return;
        }
        seller.ReceiptSize = size == "A5" ? null : size;
        await ReplyAsync(seller,
            $"✅ Receipt ab {size} mein banegi. \"receipt\" likh kar dekhein." +
            (IsThermalSize(size) ? "\n🖨️ Print karne ka tareeqa: \"printer\" likhein." : ""), ct);
    }

    private async Task HandleReceiptFooterAsync(Seller seller, string? text, CancellationToken ct)
    {
        if (ClearWord(text))
        {
            seller.ReceiptFooter = null;
            await ReplyAsync(seller, "✅ Receipt ki aakhri line ab default (Thank you) hogi.", ct);
            return;
        }
        var footer = string.Join(" ", (text ?? "").Split(new[] { '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (footer.Length == 0)
        {
            await ReplyAsync(seller, "Footer likhein, jaise: receipt footer Shukriya! Wapsi 7 din mein.", ct);
            return;
        }
        if (footer.Length > MaxFooterLength)
        {
            await ReplyAsync(seller, $"Footer {MaxFooterLength} huroof se chhota rakhein (abhi {footer.Length}).", ct);
            return;
        }
        seller.ReceiptFooter = footer;
        await ReplyAsync(seller, $"✅ Har receipt ke neeche likha aayega:\n\"{footer}\"", ct);
    }

    /// <summary>What a seller types for a header or contact line: several typed lines become one, joined with " · ".</summary>
    private static string OneReceiptLine(string? text) =>
        string.Join(" · ", (text ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private async Task HandleReceiptHeaderAsync(Seller seller, string? text, CancellationToken ct)
    {
        if (ClearWord(text))
        {
            seller.ReceiptHeader = null;
            await ReplyAsync(seller, "✅ Receipt par ab dukan ke naam ke neeche koi header nahi chhapega.", ct);
            return;
        }
        var line = OneReceiptLine(text);
        if (line.Length == 0)
        {
            await ReplyAsync(seller, string.IsNullOrWhiteSpace(seller.ReceiptHeader)
                ? "Header likhein, jaise: receipt header Gulberg Branch · Est. 2015"
                : $"🧾 Abhi header: \"{seller.ReceiptHeader}\"\nBadalne ke liye: receipt header <line>  (hatane ke liye: receipt header off)", ct);
            return;
        }
        if (line.Length > MaxHeaderLength)
        {
            await ReplyAsync(seller, $"Header {MaxHeaderLength} huroof se chhota rakhein (abhi {line.Length}).", ct);
            return;
        }
        seller.ReceiptHeader = line;
        await ReplyAsync(seller, $"✅ Receipt par dukan ke naam ke neeche likha aayega:\n\"{line}\"", ct);
    }

    private async Task HandleReceiptContactAsync(Seller seller, string? text, CancellationToken ct)
    {
        if (ClearWord(text))
        {
            seller.ReceiptContact = null;
            await ReplyAsync(seller, "✅ Receipt par ab contact line nahi chhapegi.", ct);
            return;
        }
        var line = OneReceiptLine(text);
        if (line.Length == 0)
        {
            await ReplyAsync(seller, string.IsNullOrWhiteSpace(seller.ReceiptContact)
                ? "Contact likhein, jaise: receipt contact 0300-1234567 · ali@shop.com · Gulberg, Lahore"
                : $"📞 Abhi contact: \"{seller.ReceiptContact}\"\nBadalne ke liye: receipt contact <line>  (hatane ke liye: receipt contact off)", ct);
            return;
        }
        if (line.Length > MaxContactLength)
        {
            await ReplyAsync(seller, $"Contact {MaxContactLength} huroof se chhota rakhein (abhi {line.Length}).", ct);
            return;
        }
        seller.ReceiptContact = line;
        await ReplyAsync(seller, $"✅ Receipt par contact ye chhapega:\n\"{line}\"", ct);
    }

    private async Task HandleOrderNoteAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, cmd.Number!.Value);
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, $"Order #{cmd.Number} nahi mila. Apne orders dekhne ke liye \"orders today\" likhein.", ct);
            return;
        }

        if (ClearWord(cmd.Text))
        {
            order.Notes = null;
            await ReplyAsync(seller, $"✅ Order #{order.Id} ka note hata diya.", ct);
            return;
        }
        var note = (cmd.Text ?? "").Trim();
        if (note.Length == 0)
        {
            await ReplyAsync(seller, order.Notes is null ? $"Order #{order.Id} par koi note nahi. Likhein: note {order.Id} gift wrap" : $"📝 Order #{order.Id}: {order.Notes}", ct);
            return;
        }
        if (note.Length > MaxNoteLength)
        {
            await ReplyAsync(seller, $"Note {MaxNoteLength} huroof se chhota rakhein (abhi {note.Length}).", ct);
            return;
        }
        order.Notes = note;
        await ReplyAsync(seller, $"📝 Order #{order.Id} ka note save: {note}\nYeh receipt par bhi chhapega." +
                                 (fromList ? $"\nℹ️ \"{cmd.Number}\" aapki last list ka number tha." : ""), ct);
    }

    private async Task HandleSetEmailAsync(Seller seller, string? text, CancellationToken ct)
    {
        if (ClearWord(text))
        {
            seller.Email = null;
            await ReplyAsync(seller, "✅ Aapka email hata diya.", ct);
            return;
        }
        var address = (text ?? "").Trim();
        if (address.Length > 254 || !EmailAddress.IsMatch(address))
        {
            await ReplyAsync(seller, "Email theek nahi lagta — jaise: email ali@shop.com", ct);
            return;
        }
        seller.Email = address.ToLowerInvariant();
        await ReplyAsync(seller,
            $"✅ Email save: {seller.Email}\n" +
            (_email is { IsConfigured: true }
                ? "Receipt email karne ke liye likhein: email receipt 12"
                : "ℹ️ Email bhejne ki service abhi server par on nahi hai; on hote hi \"email receipt 12\" chalega."), ct);
    }

    private async Task HandleEmailReceiptAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        if (_email is not { IsConfigured: true })
        {
            await ReplyAsync(seller, "Email bhejne ki service abhi on nahi hai. Receipt WhatsApp par mil jayegi: \"receipt\" likhein.", ct);
            return;
        }
        if (_receiptPdf is null)
        {
            await ReplyAsync(seller, "PDF receipt abhi available nahi hai.", ct);
            return;
        }

        var to = string.IsNullOrWhiteSpace(cmd.Text2) ? seller.Email : cmd.Text2.Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            await ReplyAsync(seller, "Pehle apna email set karein: email ali@shop.com\nYa seedha likhein: email receipt 12 to buyer@gmail.com", ct);
            return;
        }
        if (to.Length > 254 || !EmailAddress.IsMatch(to))
        {
            await ReplyAsync(seller, "Email theek nahi lagta — jaise: email receipt 12 to buyer@gmail.com", ct);
            return;
        }

        var (order, fromList) = await FindReceiptOrderAsync(seller, ctx, new ParsedCommand { Kind = CommandKind.Receipt, Number = cmd.Number }, ct);
        if (order is null)
        {
            await ReplyAsync(seller, cmd.Number is not null ? $"Order #{cmd.Number} nahi mila. Apne orders dekhne ke liye \"orders today\" likhein." : "Abhi koi order nahi hai.", ct);
            return;
        }

        if (!TryTakeEmailSlot(seller.Id))
        {
            await ReplyAsync(seller, $"Ek ghante mein {EmailsPerHour} emails ki hadd poori ho gayi — thori der baad try karein.", ct);
            return;
        }

        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        var (pdf, numberIsNew) = await RenderReceiptAsync(seller, order, branding, ct);
        var business = seller.BusinessName ?? "Order Receipt";
        var kind = order.SalesTaxRate > 0 ? "Tax invoice" : "Receipt";
        var sent = await _email.SendAsync(to,
            $"{kind} No. {order.ReceiptNumber} — {business}",
            $"{business} ki taraf se {kind.ToLowerInvariant()} No. {order.ReceiptNumber} (Order #{order.Id}) ka PDF attach hai.\n" +
            $"Customer: {order.Customer?.Name}\nTotal: {Formatters.Money(order.Total)}\n\n" +
            $"Sawal ho to WhatsApp: {seller.WhatsAppPhoneNumber}",
            $"Receipt-{order.Id}.pdf", pdf, "application/pdf", ct);
        if (!sent)
        {
            if (numberIsNew) order.ReceiptNumber = null; // never burn a serial number on a receipt that did not go out
            await ReplyAsync(seller, "⚠️ Email bhej nahi saka — thori der baad dobara try karein, ya \"receipt\" likh kar WhatsApp par lein.", ct);
            return;
        }

        await ReplyAsync(seller, $"📧 {kind} No. {order.ReceiptNumber} (Order #{order.Id}) {to} par bhej di." +
                                 (fromList ? $"\nℹ️ \"{cmd.Number}\" aapki last list ka number tha." : ""), ct);
    }

    /// <summary>At most <see cref="EmailsPerHour"/> sends per seller per rolling hour, so the shared mail account cannot be used to spam. In memory: a restart resets it.</summary>
    private bool TryTakeEmailSlot(int sellerId)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (EmailSendTimes)
        {
            if (!EmailSendTimes.TryGetValue(sellerId, out var times)) EmailSendTimes[sellerId] = times = new Queue<DateTime>();
            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromHours(1)) times.Dequeue();
            if (times.Count >= EmailsPerHour) return false;
            times.Enqueue(now);
            return true;
        }
    }

    private async Task HandlePrinterHelpAsync(Seller seller, CancellationToken ct)
    {
        await ReplyAsync(seller,
            "🖨️ Thermal printer se receipt print karna\n\n" +
            "1️⃣ Printer ke roll ke mutabiq size set karein:\n" +
            "• 80mm roll (aam, 3 inch): receipt size 80mm\n" +
            "• 58mm roll (chota, 2 inch): receipt size 58mm\n" +
            "• Sada A4/A5 printer: receipt size A4 ya A5\n\n" +
            "2️⃣ \"receipt 12\" likhein — PDF aayegi.\n\n" +
            "3️⃣ PDF khol kar Share / Print karein:\n" +
            "• Bluetooth thermal printer: Android par RawBT ya \"Bluetooth Print\" app mein share karein, printer pair karein, Print dabayein.\n" +
            "• WiFi/USB printer: phone ki Print service (Settings → Printing) ya printer ki apni app.\n" +
            "• Computer par: email ali@shop.com set karein, phir \"email receipt 12\" — email kholkar print karein.\n\n" +
            "4️⃣ Pehle ek test receipt chhapein. Kat jaye ya chhoti aaye to 80mm/58mm dobara check karein, aur print app mein \"Actual size\" chunein (\"Fit to page\" nahi).\n\n" +
            "Apni line: receipt footer ... · Order ka note: note 12 ... · Sab settings: receipt settings\n\n" +
            "ℹ️ Bot printer se seedha nahi jud sakta — print aapke phone/computer ki app se hota hai.", ct);
    }
}
