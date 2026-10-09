using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Infrastructure.WhatsApp;

namespace OrderTrackerBot.Api.Controllers;

[ApiController]
[Route("webhook/whatsapp")]
public class WhatsAppWebhookController : ControllerBase
{
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppWebhookController> _logger;
    private readonly WebhookInbox _inbox;

    public WhatsAppWebhookController(IOptions<WhatsAppOptions> options, ILogger<WhatsAppWebhookController> logger, WebhookInbox inbox)
    {
        _inbox = inbox;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Meta's one-time webhook verification handshake (Cloud API setup).</summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && verifyToken == _options.VerifyToken && challenge is not null)
            return Content(challenge, "text/plain");

        return StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Inbound message/status notifications from the Cloud API. Validates the signature, records each message durably
    /// (<see cref="WebhookInbox"/>), hands it to the background worker and answers 200 at once — so a slow AI call never makes Meta
    /// time out and retry, and a restart cannot lose an accepted message. A full queue answers 503 so Meta retries later.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var signature = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!WhatsAppSignatureValidator.IsValid(_options.AppSecret, System.Text.Encoding.UTF8.GetBytes(rawBody), signature))
        {
            _logger.LogWarning("Rejected webhook call with invalid signature.");
            return Unauthorized();
        }

        var payload = System.Text.Json.JsonSerializer.Deserialize<WhatsAppWebhookPayload>(rawBody);
        if (payload is null) return Ok();

        var messages = new List<WebhookMessage>();
        foreach (var (from, text, id) in payload.ExtractTextMessages())
            messages.Add(new WebhookMessage(IdOrNew(id), WebhookMessageKind.Text, from, Text: text));
        foreach (var (from, mediaId, caption, id) in payload.ExtractImageMessages())
            messages.Add(new WebhookMessage(IdOrNew(id), WebhookMessageKind.Image, from, MediaId: mediaId, Caption: caption));
        foreach (var (from, mediaId, id) in payload.ExtractAudioMessages())
            messages.Add(new WebhookMessage(IdOrNew(id), WebhookMessageKind.Audio, from, MediaId: mediaId));
        foreach (var (from, mediaId, fileName, mimeType, id) in payload.ExtractDocumentMessages())
            messages.Add(new WebhookMessage(IdOrNew(id), WebhookMessageKind.Document, from, MediaId: mediaId, FileName: fileName, MimeType: mimeType));
        foreach (var (from, json, id) in payload.ExtractFlowSubmissions())
            messages.Add(new WebhookMessage(IdOrNew(id), WebhookMessageKind.Flow, from, Json: json));
        foreach (var (from, type, id) in payload.ExtractUnsupportedMessages())
            messages.Add(new WebhookMessage(IdOrNew(id), WebhookMessageKind.Unsupported, from, Type: type));

        var accepted = true;
        foreach (var message in messages)
        {
            // Not tied to the request token: once Meta's payload is read, recording it must not be cancelled by a dropped connection.
            var result = await _inbox.AcceptAsync(message, CancellationToken.None);
            if (result == InboxResult.QueueFull)
            {
                _logger.LogError("Webhook queue is full; asking Meta to retry the message from {From}", message.From);
                accepted = false;
            }
        }

        return accepted ? Ok() : StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static string IdOrNew(string? id) => string.IsNullOrWhiteSpace(id) ? $"noid-{Guid.NewGuid():N}" : id;
}
