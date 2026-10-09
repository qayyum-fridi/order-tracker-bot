using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Alerts;

/// <summary>Emails ErrorLog:EmailTo when a new seller registers. The send runs in the background so SMTP latency never delays the seller's first reply.</summary>
public sealed class NewSellerEmailNotifier : INewSellerNotifier
{
    private readonly IErrorMailer _mailer;
    private readonly ErrorLogOptions _options;
    private readonly ILogger<NewSellerEmailNotifier> _logger;

    public NewSellerEmailNotifier(IErrorMailer mailer, IOptions<ErrorLogOptions> options, ILogger<NewSellerEmailNotifier> logger)
    {
        _mailer = mailer;
        _options = options.Value;
        _logger = logger;
    }

    public Task SellerRegisteredAsync(string phone, int totalSellers, CancellationToken cancellationToken = default)
    {
        _ = Task.Run(() => SendAsync(phone, totalSellers, DateTime.UtcNow, CancellationToken.None));
        return Task.CompletedTask;
    }

    public async Task SendAsync(string phone, int totalSellers, DateTime nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), ResolveZone(_options.TimeZoneId));
            var body = "A new seller just started on the Order Tracker bot.\n\n" +
                       $"Phone: {phone}\n" +
                       $"Time: {local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} (local) / {nowUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC\n" +
                       $"Sellers in total: {totalSellers}\n";
            await _mailer.SendAsync($"[Order Tracker] New seller: {phone}", body, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not email the new-seller notice for {Phone}", phone);
        }
    }

    private static TimeZoneInfo ResolveZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }
}
