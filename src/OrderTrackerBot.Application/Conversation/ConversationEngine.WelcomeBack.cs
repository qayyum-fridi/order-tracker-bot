using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Retention: a seller who comes back after a long silence is greeted once, as a reply to the message they just sent. It is never a push
// (outside WhatsApp's 24-hour window Meta rejects free-form text anyway). "Once per gap" needs no flag: the message that brings the seller
// back is logged right away, so the next message sees a gap of seconds.
public partial class ConversationEngine
{
    /// <summary>A seller whose previous message is at least this old is welcomed back.</summary>
    internal static readonly TimeSpan WelcomeBackGap = TimeSpan.FromDays(30);

    /// <summary>When the seller last wrote (UTC), or null for a seller never seen before.</summary>
    private Task<DateTime?> LastInboundAtAsync(string phone, CancellationToken ct) =>
        _db.MessageLogs.AsNoTracking().Where(m => m.Phone == phone && m.Direction == "inbound").MaxAsync(m => (DateTime?)m.CreatedAt, ct);

    private async Task SendWelcomeBackIfDueAsync(Seller seller, DateTime? previousInboundAt, CancellationToken ct)
    {
        // A seller still in setup gets the setup questions, not a greeting; a first message ever has no gap.
        if (!seller.OnboardingComplete || previousInboundAt is not { } previous) return;
        if (_clock.GetUtcNow().UtcDateTime - previous < WelcomeBackGap) return;

        await ReplyAsync(seller, MessageTemplates.Get("welcome-back.001"), ct);
    }
}
