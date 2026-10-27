using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// "email export [orders 30 days] [to addr]" and "email summary [to addr]": the Excel export or today's summary, mailed as a message.
// No address = the seller's own (set with "email ali@shop.com"). Same sender, address checks and hourly limit as "email receipt" (ConversationEngine.ReceiptSettings.cs).
public partial class ConversationEngine
{
    private const string ExportMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private async Task HandleEmailExportAsync(Seller seller, ExportRequest request, string? typedAddress, CancellationToken ct)
    {
        if (_exportWriter is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("export.001"), ct);
            return;
        }
        var to = await ReportAddressAsync(seller, typedAddress, ct);
        if (to is null) return;

        var file = await BuildExportAsync(seller, request, ct);
        if (file.Bytes is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("export.003", file.Summary), ct);
            return;
        }

        var business = seller.BusinessName ?? "Order Tracker";
        var sent = await _email!.SendAsync(to, MessageTemplates.Get("email.005", file.Summary),
            MessageTemplates.Get("email.004", business, file.Summary, seller.WhatsAppPhoneNumber),
            file.FileName, file.Bytes, ExportMimeType, ct);
        await ReplyAsync(seller, sent ? MessageTemplates.Get("email.002", file.Summary, to) : MessageTemplates.Get("email.003"), ct);
    }

    private async Task HandleEmailSummaryAsync(Seller seller, string? typedAddress, CancellationToken ct)
    {
        var to = await ReportAddressAsync(seller, typedAddress, ct);
        if (to is null) return;

        var (text, _) = await BuildTodaysSummaryAsync(seller, null, ct);
        var business = seller.BusinessName ?? "Order Tracker";
        var sent = await _email!.SendAsync(to, MessageTemplates.Get("email.006", business),
            MessageTemplates.Get("email.007", text, seller.WhatsAppPhoneNumber), null, null, null, ct);
        await ReplyAsync(seller, sent ? MessageTemplates.Get("email.002", MessageTemplates.Get("email.008"), to) : MessageTemplates.Get("email.003"), ct);
    }

    /// <summary>
    /// The address an emailed report goes to, or null after the seller has been told why it cannot be sent (no SMTP, no address, a bad address,
    /// or the hourly email limit). Takes one slot from the limit when it returns an address.
    /// </summary>
    private async Task<string?> ReportAddressAsync(Seller seller, string? typedAddress, CancellationToken ct)
    {
        if (_email is not { IsConfigured: true })
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.021"), ct);
            return null;
        }
        var to = string.IsNullOrWhiteSpace(typedAddress) ? seller.Email : typedAddress.Trim();
        if (string.IsNullOrWhiteSpace(to))
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.022"), ct);
            return null;
        }
        if (to.Length > 254 || !EmailAddress.IsMatch(to))
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.023"), ct);
            return null;
        }
        if (!TryTakeEmailSlot(seller.Id))
        {
            await ReplyAsync(seller, MessageTemplates.Get("receipt-settings.024", EmailsPerHour), ct);
            return null;
        }
        return to;
    }
}
