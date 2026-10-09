using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace OrderTrackerBot.Infrastructure.Alerts;

public interface IErrorMailer
{
    Task SendAsync(string subject, string body, CancellationToken cancellationToken);
}

/// <summary>Plain SMTP with STARTTLS (Gmail: smtp.gmail.com:587 with an app password). The sender is the SMTP user.</summary>
public sealed class SmtpErrorMailer : IErrorMailer
{
    private readonly ErrorLogOptions _options;

    public SmtpErrorMailer(IOptions<ErrorLogOptions> options) => _options = options.Value;

    public async Task SendAsync(string subject, string body, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_options.SmtpUser, _options.SmtpPassword),
            Timeout = 30_000
        };
        using var message = new MailMessage(_options.SmtpUser, _options.EmailTo, subject, body);
        await client.SendMailAsync(message, cancellationToken);
    }
}
