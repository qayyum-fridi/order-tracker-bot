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
    private readonly ConversationEngine _engine;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppWebhookController> _logger;
    private readonly IIssueReporter _issues;
    private readonly WebhookMessageGate _gate;

    public WhatsAppWebhookController(ConversationEngine engine, IOptions<WhatsAppOptions> options, ILogger<WhatsAppWebhookController> logger, IIssueReporter issues,
        WebhookMessageGate gate)
    {
        _gate = gate;
        _issues = issues;
        _engine = engine;
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

    /// <summary>Inbound message/status notifications from the Cloud API.</summary>
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

        foreach (var (from, text, messageId) in payload.ExtractTextMessages())
        {
            try
            {
                await _gate.RunOnceAsync(from, messageId, () => _engine.HandleIncomingMessageAsync(from, text, ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process inbound WhatsApp message from {From}", from);
                await _issues.ReportAsync(IssueCodes.InboundMessageFailed, from, null, ex, ct);
                await _engine.SendSystemErrorAsync(from, IssueCodes.InboundMessageFailed.Code, ct);
            }
        }

        foreach (var (from, mediaId, caption, messageId) in payload.ExtractImageMessages())
        {
            try
            {
                await _gate.RunOnceAsync(from, messageId, () => _engine.HandleImageMessageAsync(from, mediaId, caption, ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process screenshot from {From}", from);
                await _issues.ReportAsync(IssueCodes.ScreenshotFailed, from, null, ex, ct);
                await _engine.SendSystemErrorAsync(from, IssueCodes.ScreenshotFailed.Code, ct);
            }
        }

        foreach (var (from, mediaId, messageId) in payload.ExtractAudioMessages())
        {
            try
            {
                await _gate.RunOnceAsync(from, messageId, () => _engine.HandleAudioMessageAsync(from, mediaId, ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process voice note from {From}", from);
                await _issues.ReportAsync(IssueCodes.VoiceNoteFailed, from, null, ex, ct);
                await _engine.SendSystemErrorAsync(from, IssueCodes.VoiceNoteFailed.Code, ct);
            }
        }

        foreach (var (from, json, messageId) in payload.ExtractFlowSubmissions())
        {
            try
            {
                await _gate.RunOnceAsync(from, messageId, () => _engine.HandleFlowSubmissionAsync(from, json, ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process WhatsApp Flow submission from {From}", from);
                await _issues.ReportAsync(IssueCodes.FlowSubmissionFailed, from, null, ex, ct);
                await _engine.SendSystemErrorAsync(from, IssueCodes.FlowSubmissionFailed.Code, ct);
            }
        }

        foreach (var (from, type, messageId) in payload.ExtractUnsupportedMessages())
        {
            try
            {
                await _gate.RunOnceAsync(from, messageId, () => _engine.HandleUnsupportedMediaAsync(from, type, ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reply to unsupported {Type} message from {From}", type, from);
                await _issues.ReportAsync(IssueCodes.UnsupportedMediaReplyFailed, from, $"media type: {type}", ex, ct);
            }
        }

        // Meta expects a fast 200 regardless of downstream processing outcome, or it will retry/disable the webhook.
        return Ok();
    }
}
