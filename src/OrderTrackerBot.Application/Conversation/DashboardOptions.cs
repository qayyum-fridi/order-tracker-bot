namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// The client dashboard's address, sent with the login code as a tappable link. Unset = the code goes out without a link.
/// The code itself is never put in the link.
/// </summary>
public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    /// <summary>Absolute https address, e.g. https://dashboard.example.com.</summary>
    public string? Url { get; set; }
}
