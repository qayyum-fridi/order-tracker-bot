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

    /// <summary>Ids of the published WhatsApp Flows (forms) for detailed entry; empty = form not set up yet.</summary>
    public string ProductFlowId { get; set; } = "";
    public string CustomerFlowId { get; set; } = "";
    public string OrderFlowId { get; set; } = "";
    /// <summary>Send Flows in draft mode (for testing unpublished Flows); Meta only delivers drafts to test recipients.</summary>
    public bool FlowDraftMode { get; set; }

    /// <summary>Approved marketing template used for broadcasts; its body must take one {{1}} parameter (the message). Empty = broadcasts are recorded but not sent.</summary>
    public string BroadcastTemplateName { get; set; } = "";
    public string BroadcastTemplateLanguage { get; set; } = "en";
}
