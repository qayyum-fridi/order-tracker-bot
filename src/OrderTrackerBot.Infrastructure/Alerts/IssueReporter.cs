using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Alerts;

/// <summary>
/// Logs every issue as "[OTB-xxxx] ..." and, when FounderAlerts:WebhookUrl is set, posts it there
/// (same webhook as founder alerts — `message` stays human-readable for n8n -> Telegram/email/WhatsApp).
/// The same code + seller is only posted once per cooldown so an outage doesn't flood the founder.
/// </summary>
public class IssueReporter : IIssueReporter
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, DateTime> LastSent = new();

    private readonly HttpClient _httpClient;
    private readonly FounderAlertOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<IssueReporter> _logger;
    private readonly IErrorLogSink? _errorLog;

    public IssueReporter(HttpClient httpClient, IOptions<FounderAlertOptions> options, IServiceScopeFactory scopes, ILogger<IssueReporter> logger,
        IErrorLogSink? errorLog = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _scopes = scopes;
        _logger = logger;
        _errorLog = errorLog;
    }

    public async Task ReportAsync(IssueCode code, string? sellerPhone, string? detail = null, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var error = exception is null ? null : $"{exception.GetType().Name}: {Truncate(exception.Message, 300)}";
            _logger.Log(code.Severity == IssueSeverity.Error ? LogLevel.Error : LogLevel.Warning, exception,
                "[{Code}] {Title} | seller={Phone} | {Detail}", code.Code, code.Title, sellerPhone ?? "-", detail ?? error ?? "-");

            var postToWebhook = !string.IsNullOrWhiteSpace(_options.WebhookUrl) && ShouldSend(code, sellerPhone);
            if (!postToWebhook && _errorLog is null) return;

            var (sellerId, businessName) = await LookupSellerAsync(sellerPhone, cancellationToken);
            // Every occurrence goes to the error log; only the webhook is rate-limited by the cooldown.
            _errorLog?.Add(new ErrorLogEntry(DateTime.UtcNow, code.Code, code.Title, code.Severity, sellerPhone, businessName, detail, error));
            if (!postToWebhook) return;

            var text = $"{(code.Severity == IssueSeverity.Error ? "🚨" : "⚠️")} [{code.Code}] {code.Title}\n" +
                       $"Seller: {businessName ?? "unknown"}{(sellerPhone is null ? "" : $" ({sellerPhone})")}\n" +
                       (string.IsNullOrEmpty(detail) ? "" : $"Detail: {Truncate(detail, 300)}\n") +
                       (error is null ? "" : $"Error: {error}\n") +
                       $"Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\nTrace: MessageLogs where Phone = '{sellerPhone ?? "-"}'";

            await _httpClient.PostAsJsonAsync(_options.WebhookUrl, new
            {
                seller_id = sellerId,
                message = text,
                code = code.Code,
                title = code.Title,
                severity = code.Severity.ToString().ToLowerInvariant(),
                seller_phone = sellerPhone,
                business_name = businessName,
                detail,
                error,
                occurred_at = DateTime.UtcNow
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report issue {Code}", code.Code);
        }
    }

    private static bool ShouldSend(IssueCode code, string? phone)
    {
        var now = DateTime.UtcNow;
        if (LastSent.Count > 500)
            foreach (var old in LastSent.Where(kv => now - kv.Value > Cooldown).Select(kv => kv.Key).ToList())
                LastSent.TryRemove(old, out _);

        var key = $"{code.Code}|{phone}";
        var send = true;
        LastSent.AddOrUpdate(key, now, (_, last) =>
        {
            send = now - last > Cooldown;
            return send ? now : last;
        });
        return send;
    }

    // A fresh scope: the request's DbContext may be in a failed state after the very exception being reported.
    private async Task<(int SellerId, string? BusinessName)> LookupSellerAsync(string? phone, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(phone)) return (0, null);
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var seller = await db.Sellers.Where(s => s.WhatsAppPhoneNumber == phone)
                .Select(s => new { s.Id, s.BusinessName }).FirstOrDefaultAsync(ct);
            return seller is null ? (0, null) : (seller.Id, seller.BusinessName);
        }
        catch
        {
            return (0, null);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
