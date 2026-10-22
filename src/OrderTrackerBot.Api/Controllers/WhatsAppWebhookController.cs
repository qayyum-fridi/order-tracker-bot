using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Infrastructure.WhatsApp;

namespace OrderTrackerBot.Api.Controllers;

[ApiController]
[Route("webhook/whatsapp")]
public class WhatsAppWebhookController : ControllerBase
{
    /// <summary>Meta's own ceiling for a text message. A longer text is not genuine and never reaches the queue.</summary>
    private const int MaxTextLength = 4096;
    /// <summary>Upper bound for one webhook POST (Meta's real payloads are a few KB); checked before the body is read.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppWebhookController> _logger;
    private readonly WebhookInbox _inbox;
    private readonly IHostEnvironment _environment;

    public WhatsAppWebhookController(IOptions<WhatsAppOptions> options, ILogger<WhatsAppWebhookController> logger, WebhookInbox inbox, IHostEnvironment environment)
    {
        _inbox = inbox;
        _options = options.Value;
        _logger = logger;
        _environment = environment;
    }

    /// <summary>Meta's one-time webhook verification handshake (Cloud API setup).</summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        // An unset VerifyToken must not match an empty one: the handshake is only open once a real token is configured.
        if (mode == "subscribe" && !string.IsNullOrEmpty(_options.VerifyToken) && verifyToken == _options.VerifyToken && challenge is not null)
            return Content(challenge, "text/plain");

        return StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Inbound message/status notifications from the Cloud API. Validates the signature, records each message durably
    /// (<see cref="WebhookInbox"/>), hands it to the background worker and answers 200 at once — so a slow AI call never makes Meta
    /// time out and retry, and a restart cannot lose an accepted message. A full queue answers 503 so Meta retries later.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        // The raw bytes, not a re-encoded string: Meta's signature covers exactly the bytes it sent.
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var rawBody = buffer.ToArray();

        var signature = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!WhatsAppSignatureValidator.IsAllowed(_options.AppSecret, _environment.IsDevelopment(), rawBody, signature))
        {
            _logger.LogWarning("Rejected webhook call with missing or invalid signature.");
            return Unauthorized();
        }

        WhatsAppWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WhatsAppWebhookPayload>(rawBody);
        }
        catch (JsonException)
        {
            // A signed body that is not the expected JSON shape: say so, do not turn it into a 500 that Meta would retry.
            _logger.LogWarning("Rejected webhook call: body is not a valid Cloud API payload.");
            return BadRequest();
        }
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

        var sentAt = payload.SentAtById();
        var accepted = true;
        foreach (var original in messages)
        {
            if (original.Text is { Length: > MaxTextLength })
            {
                _logger.LogWarning("Dropped a text message of {Length} characters from {From}; WhatsApp allows {Max}.", original.Text.Length, original.From, MaxTextLength);
                continue;
            }
            var message = sentAt.TryGetValue(original.Id, out var ts) ? original with { SentAt = ts } : original;
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
