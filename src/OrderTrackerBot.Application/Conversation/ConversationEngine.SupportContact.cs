using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// "contact support": the seller asks for a person. The request is kept (MerchantFeedbacks, the same place "feedback: ..." goes) and sent to the
// founder alert channel, and the seller is told what happens next. How to reach the team comes from the Support configuration section.
// (The SupportQuery table is something else: questions that BUYERS asked the seller, see ConversationEngine.Support.cs.)
public partial class ConversationEngine
{
    /// <summary>A second request inside this window is told the first one is already with the team; the founder is not alerted twice.</summary>
    private static readonly TimeSpan SupportRepeatWindow = TimeSpan.FromHours(6);

    private const int SupportRecentMessages = 3;
    private const int SupportMessageLength = 160;

    private async Task HandleSupportContactAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var repeat = ctx.SupportRequestedAt is { } last && now - last < SupportRepeatWindow;
        if (!repeat)
        {
            ctx.SupportRequestedAt = now;
            var about = string.IsNullOrWhiteSpace(cmd.Text) ? await RecentMessagesForSupportAsync(seller, ct) : cmd.Text.Trim();
            var text = $"[support request] {seller.BusinessName} ({seller.WhatsAppPhoneNumber}): {about}";
            _db.MerchantFeedbacks.Add(new MerchantFeedback { SellerId = seller.Id, Text = text });
            await _db.SaveChangesAsync(ct);
            await _founderAlerts.NotifyAsync(seller.Id, text, ct);
        }

        var contact = _support.ContactLine is { } line ? MessageTemplates.Get("support-contact.010", line) : "";
        var hours = _support.HoursLine is { } when ? MessageTemplates.Get("support-contact.011", when) : "";
        await ReplyAsync(seller, MessageTemplates.Get(repeat ? "support-contact.002" : "support-contact.001", contact, hours), ct);
    }

    /// <summary>What the seller wrote last (the request itself comes first), so the team sees what the problem was without asking again.</summary>
    private async Task<string> RecentMessagesForSupportAsync(Seller seller, CancellationToken ct)
    {
        var recent = await _db.MessageLogs.AsNoTracking()
            .Where(m => m.Phone == seller.WhatsAppPhoneNumber && m.Direction == "inbound")
            .OrderByDescending(m => m.Id).Take(SupportRecentMessages).Select(m => m.RawText).ToListAsync(ct);
        return string.Join(" | ", recent.Select(t => t.Length > SupportMessageLength ? t[..SupportMessageLength] : t));
    }
}
