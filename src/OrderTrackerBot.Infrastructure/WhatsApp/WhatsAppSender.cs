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

    public async Task SendTextMessageAsync(string toPhoneNumber, string text, CancellationToken cancellationToken = default) =>
        await PostAsync(toPhoneNumber, text, new SendMessageRequest
        {
            To = toPhoneNumber,
            Text = new SendMessageText { Body = text }
        }, cancellationToken);

    // Interactive messages fall back to plain text on failure so the seller is never left without a reply.
    public async Task SendButtonsMessageAsync(string toPhoneNumber, string bodyText, IReadOnlyList<string> buttonLabels, CancellationToken cancellationToken = default)
    {
        var ok = await PostAsync(toPhoneNumber, bodyText, new SendInteractiveRequest
        {
            To = toPhoneNumber,
            Interactive = new InteractiveBody
            {
                Body = new InteractiveText { Text = bodyText },
                Action = new InteractiveAction
                {
                    Buttons = buttonLabels.Take(3).Select((label, i) => new InteractiveButton
                    {
                        Reply = new InteractiveReply { Id = $"btn_{i + 1}", Title = label.Length > 20 ? label[..20] : label }
                    }).ToList()
                }
            }
        }, cancellationToken);

        if (!ok)
            await SendTextMessageAsync(toPhoneNumber, $"{bodyText}\n\n{string.Join(" / ", buttonLabels)}", cancellationToken);
    }

    public async Task SendListMessageAsync(string toPhoneNumber, string bodyText, string buttonLabel, IReadOnlyList<MenuSection> sections, CancellationToken cancellationToken = default)
    {
        var ok = await PostAsync(toPhoneNumber, bodyText, new SendListRequest
        {
            To = toPhoneNumber,
            Interactive = new ListBody
            {
                Body = new InteractiveText { Text = bodyText },
                Action = new ListAction
                {
                    Button = buttonLabel.Length > 20 ? buttonLabel[..20] : buttonLabel,
                    Sections = sections.Select(s => new ListSection
                    {
                        Title = s.Title.Length > 24 ? s.Title[..24] : s.Title,
                        Rows = s.Rows.Select(r => new ListRow
                        {
                            Id = r.Id,
                            Title = r.Title.Length > 24 ? r.Title[..24] : r.Title
                        }).ToList()
                    }).ToList()
                }
            }
        }, cancellationToken);

        if (!ok)
            await SendTextMessageAsync(toPhoneNumber, bodyText, cancellationToken);
    }

    public Task<bool> SendFlowMessageAsync(string toPhoneNumber, string flowKind, string bodyText, string ctaLabel, CancellationToken cancellationToken = default)
    {
        var (flowId, firstScreen) = flowKind switch
        {
            "product" => (_options.ProductFlowId, "PRODUCT_FORM"),
            "customer" => (_options.CustomerFlowId, "CUSTOMER_FORM"),
            "order" => (_options.OrderFlowId, "ORDER_FORM"),
            _ => ("", "")
        };
        if (string.IsNullOrWhiteSpace(flowId) || string.IsNullOrWhiteSpace(_options.AccessToken))
            return Task.FromResult(false);

        return PostAsync(toPhoneNumber, bodyText, new SendFlowRequest
        {
            To = toPhoneNumber,
            Interactive = new FlowBody
            {
                Body = new InteractiveText { Text = bodyText },
                Action = new FlowAction
                {
                    Parameters = new FlowParameters
                    {
                        FlowToken = flowKind,
                        FlowId = flowId,
                        Mode = _options.FlowDraftMode ? "draft" : null,
                        FlowCta = ctaLabel.Length > 30 ? ctaLabel[..30] : ctaLabel,
                        FlowActionPayload = new FlowActionPayload { Screen = firstScreen }
                    }
                }
            }
        }, cancellationToken);
    }

    private async Task<bool> PostAsync(string toPhoneNumber, string logText, object payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            _logger.LogWarning("WhatsApp access token not configured — skipping send to {Phone}: {Text}", toPhoneNumber, logText);
            return true;
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
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Never let an outbound send failure (network blip, WhatsApp API outage) blow up the
            // caller — the inbound message must still get processed and the conversation state saved.
            _logger.LogError(ex, "WhatsApp send threw while sending to {Phone}", toPhoneNumber);
            return false;
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

    private class SendListRequest
    {
        [JsonPropertyName("messaging_product")] public string MessagingProduct { get; set; } = "whatsapp";
        [JsonPropertyName("to")] public required string To { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "interactive";
        [JsonPropertyName("interactive")] public required ListBody Interactive { get; set; }
    }

    private class ListBody
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "list";
        [JsonPropertyName("body")] public required InteractiveText Body { get; set; }
        [JsonPropertyName("action")] public required ListAction Action { get; set; }
    }

    private class ListAction
    {
        [JsonPropertyName("button")] public required string Button { get; set; }
        [JsonPropertyName("sections")] public required List<ListSection> Sections { get; set; }
    }

    private class ListSection
    {
        [JsonPropertyName("title")] public required string Title { get; set; }
        [JsonPropertyName("rows")] public required List<ListRow> Rows { get; set; }
    }

    private class ListRow
    {
        [JsonPropertyName("id")] public required string Id { get; set; }
        [JsonPropertyName("title")] public required string Title { get; set; }
    }

    private class SendFlowRequest
    {
        [JsonPropertyName("messaging_product")] public string MessagingProduct { get; set; } = "whatsapp";
        [JsonPropertyName("to")] public required string To { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "interactive";
        [JsonPropertyName("interactive")] public required FlowBody Interactive { get; set; }
    }

    private class FlowBody
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "flow";
        [JsonPropertyName("body")] public required InteractiveText Body { get; set; }
        [JsonPropertyName("action")] public required FlowAction Action { get; set; }
    }

    private class FlowAction
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "flow";
        [JsonPropertyName("parameters")] public required FlowParameters Parameters { get; set; }
    }

    private class FlowParameters
    {
        [JsonPropertyName("flow_message_version")] public string FlowMessageVersion { get; set; } = "3";
        [JsonPropertyName("flow_token")] public required string FlowToken { get; set; }
        [JsonPropertyName("flow_id")] public required string FlowId { get; set; }
        [JsonPropertyName("mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; set; }
        [JsonPropertyName("flow_cta")] public required string FlowCta { get; set; }
        [JsonPropertyName("flow_action")] public string FlowAction { get; set; } = "navigate";
        [JsonPropertyName("flow_action_payload")] public required FlowActionPayload FlowActionPayload { get; set; }
    }

    private class FlowActionPayload
    {
        [JsonPropertyName("screen")] public required string Screen { get; set; }
    }

    private class InteractiveText
    {
        [JsonPropertyName("text")] public required string Text { get; set; }
    }

    private class InteractiveBody
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "button";
        [JsonPropertyName("body")] public required InteractiveText Body { get; set; }
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
