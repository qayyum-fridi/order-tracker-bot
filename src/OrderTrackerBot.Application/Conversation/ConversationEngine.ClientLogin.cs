using OrderTrackerBot.Application.Auth;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    /// <summary>"dashboard logout" on WhatsApp: ends every dashboard session for this seller, so a token seen elsewhere stops working.</summary>
    private async Task HandleClientLogoutAsync(Seller seller, CancellationToken ct)
    {
        seller.ClientApiTokenHash = null;
        await _db.SaveChangesAsync(ct);
        await ReplyAsync(seller, MessageTemplates.Get("clientlogin.logout"), ct);
    }

    /// <summary>"login" on WhatsApp: a one-time code for the client dashboard, sent only to this chat.</summary>
    private async Task HandleClientLoginAsync(Seller seller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (seller.ClientLoginCodeSentAt is { } sentAt && now - sentAt < ClientLoginCodes.ResendWait)
        {
            var secondsLeft = Math.Max(1, (int)Math.Ceiling((sentAt + ClientLoginCodes.ResendWait - now).TotalSeconds));
            await ReplyAsync(seller, MessageTemplates.Get("clientlogin.wait", secondsLeft.ToString()), ct);
            return;
        }

        var code = ClientLoginCodes.NewCode();
        seller.ClientLoginCodeHash = ClientLoginCodes.Hash(seller.Id, code);
        seller.ClientLoginCodeSentAt = now;
        seller.ClientLoginCodeExpiresAt = now + ClientLoginCodes.Lifetime;
        seller.ClientLoginAttempts = 0;
        var minutes = ((int)ClientLoginCodes.Lifetime.TotalMinutes).ToString();
        // A plain https address is tappable in WhatsApp; the code stays out of the link.
        var dashboardUrl = DashboardLink();
        await ReplyAsync(seller, dashboardUrl is null
            ? MessageTemplates.Get("clientlogin.code", code, minutes)
            : MessageTemplates.Get("clientlogin.codelink", code, minutes, dashboardUrl), ct);

        // Outbound replies are logged as plain text. Once the code is sent, the log keeps only the fact that it went out.
        foreach (var log in _db.MessageLogs.Local.Where(l => l.Direction == "outbound" && l.RawText.Contains(code)))
            log.RawText = "[dashboard login code]";
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The configured dashboard address when it is an absolute https URL; otherwise null (the code is sent without a link).</summary>
    private string? DashboardLink() =>
        Uri.TryCreate(_dashboard.Url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;
}
