namespace OrderTrackerBot.Infrastructure.WhatsApp;

/// <summary>
/// Meta-approved WhatsApp templates, keyed by the id the code uses (e.g. "broadcast"). Lives in its own file
/// (<c>whatsapp-templates.json</c>) so it can be edited or removed without touching code; changes hot-reload.
/// </summary>
public class WhatsAppTemplatesOptions : Dictionary<string, WhatsAppTemplate>
{
    public const string SectionName = "WhatsAppTemplates";
    public const string Broadcast = "broadcast";
}

public class WhatsAppTemplate
{
    /// <summary>Template name exactly as approved in Meta Business Manager. Empty = template disabled (sends are recorded, not delivered).</summary>
    public string Name { get; set; } = "";
    public string Language { get; set; } = "en";
    /// <summary>Documentation only: the approved body text with {{1}}, {{2}} placeholders, for easy reference when editing.</summary>
    public string Body { get; set; } = "";
}
