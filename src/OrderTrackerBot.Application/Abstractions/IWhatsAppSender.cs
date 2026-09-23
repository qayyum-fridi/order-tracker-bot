namespace OrderTrackerBot.Application.Abstractions;

/// <summary>A tappable list row. <paramref name="Id"/> is sent back as the seller's message text, so use a valid typed command.</summary>
public sealed record MenuRow(string Id, string Title);

public sealed record MenuSection(string Title, IReadOnlyList<MenuRow> Rows);

public interface IWhatsAppSender
{
    /// <summary>Sends a list message (max 10 rows total); the tapped row's Id arrives as a normal text message.</summary>
    Task SendListMessageAsync(string toPhoneNumber, string bodyText, string buttonLabel, IReadOnlyList<MenuSection> sections, CancellationToken cancellationToken = default);

    Task SendTextMessageAsync(string toPhoneNumber, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a WhatsApp Flow (in-chat form). <paramref name="flowKind"/> is "product", "customer" or "order" and comes back
    /// as the flow_token on submit. Returns false when that form's Flow id isn't configured or Meta rejected it.
    /// </summary>
    Task<bool> SendFlowMessageAsync(string toPhoneNumber, string flowKind, string bodyText, string ctaLabel, CancellationToken cancellationToken = default);

    /// <summary>Sends up to 3 quick-reply buttons; a tapped button arrives as a normal text message with the button's label.</summary>
    Task SendButtonsMessageAsync(string toPhoneNumber, string bodyText, IReadOnlyList<string> buttonLabels, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a Meta-approved template (the only way to message a customer outside the 24h window, e.g. a broadcast).
    /// Returns false when no template is configured or Meta rejected it.
    /// </summary>
    Task<bool> SendTemplateMessageAsync(string toPhoneNumber, IReadOnlyList<string> bodyParameters, CancellationToken cancellationToken = default);
}

/// <summary>Downloads media (screenshots) a seller sent, by the media id in the webhook.</summary>
public interface IWhatsAppMediaClient
{
    /// <summary>Returns null when media download isn't configured or failed.</summary>
    Task<(byte[] Bytes, string MimeType)?> DownloadAsync(string mediaId, CancellationToken cancellationToken = default);
}
