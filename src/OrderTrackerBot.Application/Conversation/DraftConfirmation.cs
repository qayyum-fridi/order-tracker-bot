namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// A YES/"ok" saves an order draft only while the draft is recent. Drafts are shown with a timestamp that starts on first persist.
/// A missing timestamp (a draft saved before this rule existed) is not saved.
/// </summary>
public static class DraftConfirmation
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public static bool IsFresh(DateTime? askedAtUtc, DateTime nowUtc)
    {
        if (askedAtUtc is null) return false;
        var age = nowUtc - askedAtUtc.Value;
        return age >= TimeSpan.Zero && age < Window;
    }
}
