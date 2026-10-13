namespace OrderTrackerBot.Application.Abstractions;

/// <summary>
/// Sends an email on a seller's behalf (a receipt PDF to themselves or, when they type the address, to a buyer). Off unless SMTP is configured
/// (<c>MerchantEmail:*</c>): the engine then answers that email is not enabled yet, like the other config-optional services.
/// </summary>
public interface IMerchantEmailSender
{
    bool IsConfigured { get; }

    /// <summary>True when the server accepted the message. Never throws: a failed send is a false result.</summary>
    Task<bool> SendAsync(string toAddress, string subject, string textBody, string? attachmentName, byte[]? attachment, string? attachmentMimeType,
        CancellationToken cancellationToken = default);
}
