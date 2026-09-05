using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.Alerts;

public class FounderAlertNotifier : IFounderAlertNotifier
{
    private readonly HttpClient _httpClient;
    private readonly FounderAlertOptions _options;
    private readonly ILogger<FounderAlertNotifier> _logger;

    public FounderAlertNotifier(HttpClient httpClient, IOptions<FounderAlertOptions> options, ILogger<FounderAlertNotifier> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task NotifyAsync(int sellerId, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Founder alert (seller {SellerId}): {Message}", sellerId, message);

        if (string.IsNullOrWhiteSpace(_options.WebhookUrl)) return;

        try
        {
            await _httpClient.PostAsJsonAsync(_options.WebhookUrl, new { seller_id = sellerId, message }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post founder alert to webhook.");
        }
    }
}
