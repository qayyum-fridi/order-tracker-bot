namespace OrderTrackerBot.Infrastructure.Alerts;

public class FounderAlertOptions
{
    public const string SectionName = "FounderAlerts";

    /// <summary>Optional n8n (or any) webhook URL that fans this out to Telegram/email. Left empty, alerts are just logged.</summary>
    public string? WebhookUrl { get; set; }
}
