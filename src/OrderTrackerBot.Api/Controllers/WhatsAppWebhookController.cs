using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.WhatsApp;

namespace OrderTrackerBot.Api.Controllers;

[ApiController]
[Route("webhook/whatsapp")]
public class WhatsAppWebhookController : ControllerBase
{
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppWebhookController> _logger;
    private readonly WebhookWorkQueue _queue;

    public WhatsAppWebhookController(IOptions<WhatsAppOptions> options, ILogger<WhatsAppWebhookController> logger, WebhookWorkQueue queue)
    {
        _queue = queue;
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
    /// Inbound message/status notifications from the Cloud API. Validates the signature, hands each message to the background
    /// worker (<see cref="WebhookWorkerService"/>) and answers 200 at once, so a slow AI call never makes Meta time out and retry
    /// and a dropped connection never cancels a message half-way. A full queue answers 503 so Meta retries later.
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

        var accepted = true;

        foreach (var (from, text, messageId) in payload.ExtractTextMessages())
            accepted &= Enqueue(from, messageId, IssueCodes.InboundMessageFailed, null, true,
                (engine, token) => engine.HandleIncomingMessageAsync(from, text, token));

        foreach (var (from, mediaId, caption, messageId) in payload.ExtractImageMessages())
            accepted &= Enqueue(from, messageId, IssueCodes.ScreenshotFailed, null, true,
                (engine, token) => engine.HandleImageMessageAsync(from, mediaId, caption, token));

        foreach (var (from, mediaId, messageId) in payload.ExtractAudioMessages())
            accepted &= Enqueue(from, messageId, IssueCodes.VoiceNoteFailed, null, true,
                (engine, token) => engine.HandleAudioMessageAsync(from, mediaId, token));

        foreach (var (from, mediaId, fileName, mimeType, messageId) in payload.ExtractDocumentMessages())
            accepted &= Enqueue(from, messageId, IssueCodes.UnsupportedMediaReplyFailed, "media type: document", true,
                (engine, token) => engine.HandleDocumentMessageAsync(from, mediaId, fileName, mimeType, token));

        foreach (var (from, json, messageId) in payload.ExtractFlowSubmissions())
            accepted &= Enqueue(from, messageId, IssueCodes.FlowSubmissionFailed, null, true,
                (engine, token) => engine.HandleFlowSubmissionAsync(from, json, token));

        foreach (var (from, type, messageId) in payload.ExtractUnsupportedMessages())
            accepted &= Enqueue(from, messageId, IssueCodes.UnsupportedMediaReplyFailed, $"media type: {type}", false,
                (engine, token) => engine.HandleUnsupportedMediaAsync(from, type, token));

        return accepted ? Ok() : StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private bool Enqueue(string from, string? messageId, IssueCode failureCode, string? failureDetail, bool notifySeller,
        Func<ConversationEngine, CancellationToken, Task> handle)
    {
        if (_queue.TryEnqueue(new WebhookWork(from, messageId, failureCode, failureDetail, notifySeller, handle))) return true;
        _logger.LogError("Webhook queue is full; asking Meta to retry the message from {From}", from);
        return false;
    }
}
