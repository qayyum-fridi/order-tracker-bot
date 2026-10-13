using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Email;

/// <summary>SMTP settings for emails sent on a seller's behalf (receipts). Separate from <c>ErrorLog:*</c>, which is the founder's own alert mailbox.</summary>
public sealed class MerchantEmailOptions
{
    public const string SectionName = "MerchantEmail";

    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    /// <summary>Shown as the sender name; the address is <see cref="SmtpUser"/>.</summary>
    public string FromName { get; set; } = "Order Tracker";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpUser) && !string.IsNullOrWhiteSpace(SmtpPassword);
}

/// <summary>
/// MailKit SMTP sender for receipts. Gmail's SMTP is fine to try this out, but not for real volume: use a transactional provider
/// (SES, SendGrid, Resend...) on your own domain with SPF/DKIM, or messages will land in spam and Gmail may suspend the account.
/// </summary>
public sealed class MerchantEmailSender : IMerchantEmailSender
{
    private readonly MerchantEmailOptions _options;
    private readonly ILogger<MerchantEmailSender> _logger;

    public MerchantEmailSender(IOptions<MerchantEmailOptions> options, ILogger<MerchantEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<bool> SendAsync(string toAddress, string subject, string textBody, string? attachmentName, byte[]? attachment, string? attachmentMimeType,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured) return false;
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.FromName, _options.SmtpUser));
            message.To.Add(MailboxAddress.Parse(toAddress));
            message.Subject = subject;

            var body = new BodyBuilder { TextBody = textBody };
            if (attachment is not null && !string.IsNullOrWhiteSpace(attachmentName))
                body.Attachments.Add(attachmentName, attachment, ContentType.Parse(attachmentMimeType ?? "application/octet-stream"));
            message.Body = body.ToMessageBody();

            using var client = new SmtpClient { Timeout = 30_000 };
            var tls = _options.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, tls, cancellationToken);
            await client.AuthenticateAsync(_options.SmtpUser, _options.SmtpPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Merchant email to {Address} failed", toAddress);
            return false;
        }
    }
}
