namespace OrderTrackerBot.Application.Conversation;

// Short memory (MVP): the customer the bot last updated is remembered for ten minutes, so "wahi ka address Gulberg" or
// "uska phone 0300…" can point to them. Nothing older than that is used; a name is always preferred when one is typed.
public partial class ConversationEngine
{
    private static readonly TimeSpan RecentCustomerWindow = TimeSpan.FromMinutes(10);

    /// <summary>Words that point back to the last customer ("wahi", "uska"). Only these are resolved, never a name.</summary>
    private static readonly HashSet<string> BackReferenceWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "wahi", "wohi", "wo", "woh", "us", "uska", "uski", "uske", "iska", "iski", "yehi", "yahi", "ye", "yeh"
    };

    /// <summary>The customer id a back-reference points to, when it was set within the window; otherwise null.</summary>
    private int? RecentCustomerFor(SessionContextData ctx, string text) =>
        BackReferenceWords.Contains(text.Trim()) && ctx.LastCustomerId is { } id && ctx.LastCustomerAt is { } at
        && _clock.GetUtcNow().UtcDateTime - at <= RecentCustomerWindow
            ? id
            : null;

    private void RememberCustomer(SessionContextData ctx, int customerId)
    {
        ctx.LastCustomerId = customerId;
        ctx.LastCustomerAt = _clock.GetUtcNow().UtcDateTime;
    }
}
