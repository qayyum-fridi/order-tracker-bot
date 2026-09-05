using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Infrastructure.WhatsApp;

/// <summary>Sends outbound messages via the Meta WhatsApp Cloud API.</summary>
public class WhatsAppSender : IWhatsAppSender
{
    private readonly HttpClient _httpClient;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppSender> _logger;

    public WhatsAppSender(HttpClient httpClient, IOptions<WhatsAppOptions> options, ILogger<WhatsAppSender> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendTextMessageAsync(string toPhoneNumber, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            _logger.LogWarning("WhatsApp access token not configured — skipping send to {Phone}: {Text}", toPhoneNumber, text);
            return;
        }

        var url = $"{_options.GraphApiBaseUrl.TrimEnd('/')}/{_options.PhoneNumberId}/messages";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new SendMessageRequest
            {
                To = toPhoneNumber,
                Text = new SendMessageText { Body = text }
            })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AccessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("WhatsApp send failed ({Status}) to {Phone}: {Body}", response.StatusCode, toPhoneNumber, body);
        }
    }

    private class SendMessageRequest
    {
        [JsonPropertyName("messaging_product")] public string MessagingProduct { get; set; } = "whatsapp";
        [JsonPropertyName("to")] public required string To { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "text";
        [JsonPropertyName("text")] public required SendMessageText Text { get; set; }
    }

    private class SendMessageText
    {
        [JsonPropertyName("body")] public required string Body { get; set; }
    }
}
