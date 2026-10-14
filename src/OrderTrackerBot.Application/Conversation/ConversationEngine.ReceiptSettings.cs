using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Everyday receipt settings for a seller with a thermal (or any) printer, an order note, and receipts by email.
//   receipt settings | receipt size 80mm|58mm|A5|A4 | receipt header <line>|off | receipt contact <line>|off | receipt footer <line>|off | note 12 <text>|off
//   logo / banner: a picture captioned "logo" or "banner" (ConversationEngine.Branding.cs)
//   email ali@shop.com | email off | email receipt [12] [to someone@mail.com] | printer
// The bot never prints by itself: it makes a PDF in the right paper size, and the seller prints it from their phone or computer.
public partial class ConversationEngine
{
    private static readonly Regex EmailAddress = new(@"^[^\s@,;<>]+@[^\s@,;<>]+\.[^\s@,;<>]{2,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int MaxFooterLength = 200;
    private const int MaxFooterLines = 4;
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
            MessageTemplates.Get("receipt-settings.001", NormalizeReceiptSize(seller.ReceiptSize) ?? "A5", (branding?.Logo is not null ? "set ✅" : "abhi nahi"), (branding?.Banner is not null ? "set ✅" : "abhi nahi"), Or(seller.ReceiptHeader, "nahi (sirf dukan ka naam)"), Or(seller.ReceiptContact, "nahi"), Or(seller.ReceiptFooter, "default (Thank you ...)"), seller.Email ?? "set nahi", (_email is { IsConfigured: true } ? "" : " (email service abhi server par on nahi)")), ct);
    }

    private async Task HandleReceiptSizeAsync(Seller seller, string? text, CancellationToken ct)
    {
        var size = NormalizeReceiptSize(text);
        if (size is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.002"), ct);
            return;
        }
        seller.ReceiptSize = size == "A5" ? null : size;
        await ReplyAsync(seller,
            MessageTemplates.Get("receipt-settings.003", size, (IsThermalSize(size) ? "\n🖨️ Print karne ka tareeqa: \"printer\" likhein." : "")), ct);
    }

    private async Task HandleReceiptFooterAsync(Seller seller, string? text, CancellationToken ct)
    {
        if (ClearWord(text))
        {
            seller.ReceiptFooter = null;
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.004"), ct);
            return;
        }
        // Line breaks are kept (a seller's own layout: "Shukriya!" / "Wapsi 7 din mein" / "Insta: @shop"); blank lines and tabs are dropped.
        var lines = (text ?? "").Replace('\t', ' ').Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var footer = string.Join("\n", lines);
        if (footer.Length == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.005"), ct);
            return;
        }
        if (lines.Count > MaxFooterLines)
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.006", MaxFooterLines, lines.Count), ct);
            return;
        }
        if (footer.Length > MaxFooterLength)
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.007", MaxFooterLength, footer.Length), ct);
            return;
        }
        seller.ReceiptFooter = footer;
        await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.008", footer), ct);
    }

    /// <summary>What a seller types for a header or contact line: several typed lines become one, joined with " · ".</summary>
    private static string OneReceiptLine(string? text) =>
        string.Join(" · ", (text ?? "").Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private async Task HandleReceiptHeaderAsync(Seller seller, string? text, CancellationToken ct)
    {
        if (ClearWord(text))
        {
            seller.ReceiptHeader = null;
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.009"), ct);
            return;
        }
        var line = OneReceiptLine(text);
        if (line.Length == 0)
        {
            await ReplyAsync(seller, string.IsNullOrWhiteSpace(seller.ReceiptHeader)
                ? MessageTemplates.Get("receipt-settings.028")
                : MessageTemplates.Get("receipt-settings.029", seller.ReceiptHeader), ct);
            return;
        }
        if (line.Length > MaxHeaderLength)
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.010", MaxHeaderLength, line.Length), ct);
            return;
        }
        seller.ReceiptHeader = line;
        await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.011", line), ct);
    }

    private async Task HandleReceiptContactAsync(Seller seller, string? text, CancellationToken ct)
    {
        if (ClearWord(text))
        {
            seller.ReceiptContact = null;
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.012"), ct);
            return;
        }
        var line = OneReceiptLine(text);
        if (line.Length == 0)
        {
            await ReplyAsync(seller, string.IsNullOrWhiteSpace(seller.ReceiptContact)
                ? MessageTemplates.Get("receipt-settings.030")
                : MessageTemplates.Get("receipt-settings.031", seller.ReceiptContact), ct);
            return;
        }
        if (line.Length > MaxContactLength)
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.013", MaxContactLength, line.Length), ct);
            return;
        }
        seller.ReceiptContact = line;
        await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.014", line), ct);
    }

    private async Task HandleOrderNoteAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, cmd.Number!.Value);
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.028", cmd.Number), ct);
            return;
        }

        if (ClearWord(cmd.Text))
        {
            order.Notes = null;
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.015", order.Id), ct);
            return;
        }
        var note = (cmd.Text ?? "").Trim();
        if (note.Length == 0)
        {
            await ReplyAsync(seller, order.Notes is null ? MessageTemplates.Get("receipt-settings.032", order.Id, order.Id) : MessageTemplates.Get("receipt-settings.033", order.Id, order.Notes), ct);
            return;
        }
        if (note.Length > MaxNoteLength)
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.016", MaxNoteLength, note.Length), ct);
            return;
        }
        order.Notes = note;
        await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.017", order.Id, note, (fromList ? $"\nℹ️ \"{cmd.Number}\" aapki last list ka number tha." : "")), ct);
    }

    private async Task HandleSetEmailAsync(Seller seller, string? text, CancellationToken ct)
    {
        if (ClearWord(text))
        {
            seller.Email = null;
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.018"), ct);
            return;
        }
        var address = (text ?? "").Trim();
        if (address.Length > 254 || !EmailAddress.IsMatch(address))
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.019"), ct);
            return;
        }
        seller.Email = address.ToLowerInvariant();
        await ReplyAsync(seller,
            MessageTemplates.Get("receipt-settings.020", seller.Email, (_email is { IsConfigured: true }
                ? "Receipt email karne ke liye likhein: email receipt 12"
                : "ℹ️ Email bhejne ki service abhi server par on nahi hai; on hote hi \"email receipt 12\" chalega.")), ct);
    }

    private async Task HandleEmailReceiptAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        if (_email is not { IsConfigured: true })
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.021"), ct);
            return;
        }
        if (_receiptPdf is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("branding.001"), ct);
            return;
        }

        var to = string.IsNullOrWhiteSpace(cmd.Text2) ? seller.Email : cmd.Text2.Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.022"), ct);
            return;
        }
        if (to.Length > 254 || !EmailAddress.IsMatch(to))
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.023"), ct);
            return;
        }

        var (order, fromList) = await FindReceiptOrderAsync(seller, ctx, new ParsedCommand { Kind = CommandKind.Receipt, Number = cmd.Number }, ct);
        if (order is null)
        {
            await ReplyAsync(seller, cmd.Number is not null ? MessageTemplates.Get("commands.028", cmd.Number) : MessageTemplates.Get("order-edit.014"), ct);
            return;
        }

        if (!TryTakeEmailSlot(seller.Id))
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.024", EmailsPerHour), ct);
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
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.025"), ct);
            return;
        }

        await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.026", kind, order.ReceiptNumber, order.Id, to, (fromList ? $"\nℹ️ \"{cmd.Number}\" aapki last list ka number tha." : "")), ct);
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
            MessageTemplates.Get("receipt-settings.027"), ct);
    }
}
