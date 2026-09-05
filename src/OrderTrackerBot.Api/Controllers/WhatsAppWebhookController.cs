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

        foreach (var (from, text) in payload.ExtractTextMessages())
        {
            try
            {
                await _engine.HandleIncomingMessageAsync(from, text, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process inbound WhatsApp message from {From}", from);
            }
        }

        // Meta expects a fast 200 regardless of downstream processing outcome, or it will retry/disable the webhook.
        return Ok();
    }
}
