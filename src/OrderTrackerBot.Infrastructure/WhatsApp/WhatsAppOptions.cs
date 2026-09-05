namespace OrderTrackerBot.Infrastructure.WhatsApp;

public class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>Meta Graph API base, versioned (e.g. https://graph.facebook.com/v20.0).</summary>
    public string GraphApiBaseUrl { get; set; } = "https://graph.facebook.com/v20.0";
    public string PhoneNumberId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    /// <summary>Arbitrary string you choose; must match what you enter in the Meta webhook config.</summary>
    public string VerifyToken { get; set; } = "";
    /// <summary>App secret, used to verify the X-Hub-Signature-256 header on incoming webhooks.</summary>
    public string AppSecret { get; set; } = "";
}
