using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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

    public WhatsAppWebhookController(ConversationEngine engine, IOptions<WhatsAppOptions> options, ILogger<WhatsAppWebhookController> logger)
    {
        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    // WhatsApp redelivers a webhook when it doesn't get a fast 200; drop repeats so the seller sees one reply, not two.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> SeenMessageIds = new();

    private static bool IsDuplicateDelivery(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return false;

        var now = DateTime.UtcNow;
        if (SeenMessageIds.Count > 5000)
            foreach (var old in SeenMessageIds.Where(kv => now - kv.Value > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
                SeenMessageIds.TryRemove(old, out _);

        return !SeenMessageIds.TryAdd(messageId, now);
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
            if (IsDuplicateDelivery(messageId)) continue;
            try
            {
                await _engine.HandleIncomingMessageAsync(from, text, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process inbound WhatsApp message from {From}", from);
                await _engine.SendSystemErrorAsync(from, ct);
            }
        }

        foreach (var (from, mediaId, caption, messageId) in payload.ExtractImageMessages())
        {
            if (IsDuplicateDelivery(messageId)) continue;
            try
            {
                await _engine.HandleImageMessageAsync(from, mediaId, caption, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process screenshot from {From}", from);
                await _engine.SendSystemErrorAsync(from, ct);
            }
        }

        foreach (var (from, json, messageId) in payload.ExtractFlowSubmissions())
        {
            if (IsDuplicateDelivery(messageId)) continue;
            try
            {
                await _engine.HandleFlowSubmissionAsync(from, json, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process WhatsApp Flow submission from {From}", from);
                await _engine.SendSystemErrorAsync(from, ct);
            }
        }

        foreach (var (from, type, messageId) in payload.ExtractUnsupportedMessages())
        {
            if (IsDuplicateDelivery(messageId)) continue;
            try
            {
                await _engine.HandleUnsupportedMediaAsync(from, type, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reply to unsupported {Type} message from {From}", type, from);
            }
        }

        // Meta expects a fast 200 regardless of downstream processing outcome, or it will retry/disable the webhook.
        return Ok();
    }
}
