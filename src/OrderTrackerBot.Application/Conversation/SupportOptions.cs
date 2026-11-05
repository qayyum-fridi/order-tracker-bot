namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// How a seller reaches the people behind the bot ("contact support"). Every value comes from configuration (section <c>Support</c>); nothing is
/// written in code. A value left empty is simply not shown in the reply.
/// </summary>
public sealed class SupportOptions
{
    public const string SectionName = "Support";

    /// <summary>The WhatsApp number (or wa.me link) of the support person, shown as written.</summary>
    public string? WhatsAppNumber { get; set; }

    public string? Email { get; set; }

    /// <summary>When someone answers, in words the seller reads (e.g. "Mon-Sat, 10am-6pm").</summary>
    public string? Hours { get; set; }

    /// <summary>The "how to reach us" line, or null when no detail is configured.</summary>
    public string? ContactLine
    {
        get
        {
            var parts = new[] { Clean(WhatsAppNumber), Clean(Email) }.Where(p => p is not null).ToList();
            return parts.Count == 0 ? null : string.Join(" | ", parts);
        }
    }

    public string? HoursLine => Clean(Hours);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
