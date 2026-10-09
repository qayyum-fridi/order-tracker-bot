using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace OrderTrackerBot.Infrastructure.Alerts;

public interface IErrorMailer
{
    Task SendAsync(string subject, string body, CancellationToken cancellationToken);
}

/// <summary>
/// SMTP through MailKit (Gmail: smtp.gmail.com:587 STARTTLS, or 465 implicit TLS, with an app password). The sender is the SMTP user.
/// MailKit replaces System.Net.Mail.SmtpClient, which failed the Gmail TLS handshake on Linux and hid the server's reply.
/// </summary>
public sealed class SmtpErrorMailer : IErrorMailer
{
    private readonly ErrorLogOptions _options;

    public SmtpErrorMailer(IOptions<ErrorLogOptions> options) => _options = options.Value;

    public async Task SendAsync(string subject, string body, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_options.SmtpUser));
        message.To.Add(MailboxAddress.Parse(_options.EmailTo));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient { Timeout = 30_000 };
        var tls = _options.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, tls, cancellationToken);
        await client.AuthenticateAsync(_options.SmtpUser, _options.SmtpPassword, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
