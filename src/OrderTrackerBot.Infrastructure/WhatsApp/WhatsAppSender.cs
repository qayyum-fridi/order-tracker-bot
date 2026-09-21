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

    public Task SendTextMessageAsync(string toPhoneNumber, string text, CancellationToken cancellationToken = default) =>
        PostAsync(toPhoneNumber, text, new SendMessageRequest
        {
            To = toPhoneNumber,
            Text = new SendMessageText { Body = text }
        }, cancellationToken);

    public Task SendButtonsMessageAsync(string toPhoneNumber, string bodyText, IReadOnlyList<string> buttonLabels, CancellationToken cancellationToken = default) =>
        PostAsync(toPhoneNumber, bodyText, new SendInteractiveRequest
        {
            To = toPhoneNumber,
            Interactive = new InteractiveBody
            {
                Body = new SendMessageText { Body = bodyText },
                Action = new InteractiveAction
                {
                    Buttons = buttonLabels.Take(3).Select((label, i) => new InteractiveButton
                    {
                        Reply = new InteractiveReply { Id = $"btn_{i + 1}", Title = label.Length > 20 ? label[..20] : label }
                    }).ToList()
                }
            }
        }, cancellationToken);

    private async Task PostAsync(string toPhoneNumber, string logText, object payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            _logger.LogWarning("WhatsApp access token not configured — skipping send to {Phone}: {Text}", toPhoneNumber, logText);
            return;
        }

        var url = $"{_options.GraphApiBaseUrl.TrimEnd('/')}/{_options.PhoneNumberId}/messages";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, payload.GetType())
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.AccessToken);

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("WhatsApp send failed ({Status}) to {Phone}: {Body}", response.StatusCode, toPhoneNumber, body);
            }
        }
        catch (Exception ex)
        {
            // Never let an outbound send failure (network blip, WhatsApp API outage) blow up the
            // caller — the inbound message must still get processed and the conversation state saved.
            _logger.LogError(ex, "WhatsApp send threw while sending to {Phone}", toPhoneNumber);
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

    private class SendInteractiveRequest
    {
        [JsonPropertyName("messaging_product")] public string MessagingProduct { get; set; } = "whatsapp";
        [JsonPropertyName("to")] public required string To { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "interactive";
        [JsonPropertyName("interactive")] public required InteractiveBody Interactive { get; set; }
    }

    private class InteractiveBody
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "button";
        [JsonPropertyName("body")] public required SendMessageText Body { get; set; }
        [JsonPropertyName("action")] public required InteractiveAction Action { get; set; }
    }

    private class InteractiveAction
    {
        [JsonPropertyName("buttons")] public required List<InteractiveButton> Buttons { get; set; }
    }

    private class InteractiveButton
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "reply";
        [JsonPropertyName("reply")] public required InteractiveReply Reply { get; set; }
    }

    private class InteractiveReply
    {
        [JsonPropertyName("id")] public required string Id { get; set; }
        [JsonPropertyName("title")] public required string Title { get; set; }
    }
}
