using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Short memory (MVP): the customer the bot last updated is remembered for ten minutes, so "wahi ka address Gulberg" or
// "uska phone 0300…" can point to them. Nothing older than that is used; a name is always preferred when one is typed.
public partial class ConversationEngine
{
    private static readonly TimeSpan RecentCustomerWindow = TimeSpan.FromMinutes(10);

    /// <summary>Words that point back to the last customer ("wahi", "uska"). Only these are resolved, never a name.</summary>
    private static readonly HashSet<string> BackReferenceWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "wahi", "wohi", "wo", "woh", "us", "uska", "uski", "uske", "iska", "iski", "yehi", "yahi", "ye", "yeh",
        // English
        "that", "this", "same", "it", "its",
        // Urdu script (the speech model writes these too)
        "وہی", "وہ", "اس", "اُس", "اسی", "اُسی", "یہی", "یہ"
    };

    /// <summary>The customer id a back-reference points to, when it was set within the window; otherwise null.</summary>
    private int? RecentCustomerFor(SessionContextData ctx, string text) =>
        BackReferenceWords.Contains(text.Trim()) && ctx.LastCustomerId is { } id && ctx.LastCustomerAt is { } at
        && _clock.GetUtcNow().UtcDateTime - at <= RecentCustomerWindow
            ? id
            : null;

    // "wahi order", "uska order", "wahi order dikhao": the order the seller saved or opened last, for ten minutes. Anything else is left alone.
    private static readonly System.Text.RegularExpressions.Regex BackReferenceOrder = new(
        @"^(?<w>\p{L}+)\s+(?:orders?|آرڈر|آڈر)(?:\s+(?:dikhao|dikha\s+do|dikhaiye|batao|kholo|show|details?|دکھاؤ|دکھائیں|بتاؤ|بتائیں|کھولو))?\s*[?؟]?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private void RememberOrder(SessionContextData ctx, int orderId)
    {
        ctx.LastOrderId = orderId;
        ctx.LastOrderAt = _clock.GetUtcNow().UtcDateTime;
    }

    /// <summary>Shows the remembered order when the message is a back-reference to it; false (nothing done) otherwise or when none is recent.</summary>
    private async Task<bool> TryHandleBackReferenceOrderAsync(Seller seller, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (BackReferenceOrder.Match(message.Trim()) is not { Success: true } m || !BackReferenceWords.Contains(m.Groups["w"].Value)) return false;
        if (ctx.LastOrderId is not { } id || ctx.LastOrderAt is not { } at || _clock.GetUtcNow().UtcDateTime - at > RecentCustomerWindow) return false;
        await HandleOrderDetailAsync(seller, ctx, id, ct, resolveList: false);
        return true;
    }

    private void RememberCustomer(SessionContextData ctx, int customerId)
    {
        ctx.LastCustomerId = customerId;
        ctx.LastCustomerAt = _clock.GetUtcNow().UtcDateTime;
    }
}
