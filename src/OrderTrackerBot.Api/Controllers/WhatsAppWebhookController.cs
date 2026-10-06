using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.WhatsApp;

namespace OrderTrackerBot.Api.Controllers;

[ApiController]
[Route("webhook/whatsapp")]
public class WhatsAppWebhookController : ControllerBase
{
    private readonly ConversationEngine _engine;
    private readonly IAppDbContext _db;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppWebhookController> _logger;

    public WhatsAppWebhookController(ConversationEngine engine, IAppDbContext db, IOptions<WhatsAppOptions> options, ILogger<WhatsAppWebhookController> logger)
    {
        _engine = engine;
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    // Per-sender gate so two rapid messages can't race on ConversationSession.State (in-process; multi-instance needs sticky routing or a DB lock).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> SenderGates = new();

    // WhatsApp redelivers a webhook when it doesn't get a fast 200; the message id is claimed in the DB (PK) so repeats are dropped across restarts/instances.
    private async Task<bool> IsDuplicateDeliveryAsync(string? messageId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(messageId)) return false;

        var claim = new ProcessedWebhookMessage { MessageId = messageId };
        _db.ProcessedWebhookMessages.Add(claim);
        try
        {
            await _db.SaveChangesAsync(ct);
            return false;
        }
        catch (DbUpdateException)
        {
            _db.ProcessedWebhookMessages.Remove(claim); // Added -> detached
            return true;
        }
    }

    private async Task ProcessAsync(string from, string? messageId, Func<Task> handle, CancellationToken ct)
    {
        var gate = SenderGates.GetOrAdd(from, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (await IsDuplicateDeliveryAsync(messageId, ct)) return;
            await handle();
        }
        finally
        {
            gate.Release();
        }
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
                await ProcessAsync(from, messageId, () => _engine.HandleIncomingMessageAsync(from, text, ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process inbound WhatsApp message from {From}", from);
                await _engine.SendSystemErrorAsync(from, ct);
            }
        }

        foreach (var (from, mediaId, caption, messageId) in payload.ExtractImageMessages())
        {
            try
            {
                await ProcessAsync(from, messageId, () => _engine.HandleImageMessageAsync(from, mediaId, caption, ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process screenshot from {From}", from);
                await _engine.SendSystemErrorAsync(from, ct);
            }
        }

        foreach (var (from, mediaId, messageId) in payload.ExtractAudioMessages())
        {
            try
            {
                await ProcessAsync(from, messageId, () => _engine.HandleAudioMessageAsync(from, mediaId, ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process voice note from {From}", from);
                await _engine.SendSystemErrorAsync(from, ct);
            }
        }

        foreach (var (from, json, messageId) in payload.ExtractFlowSubmissions())
        {
            try
            {
                await ProcessAsync(from, messageId, () => _engine.HandleFlowSubmissionAsync(from, json, ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process WhatsApp Flow submission from {From}", from);
                await _engine.SendSystemErrorAsync(from, ct);
            }
        }

        foreach (var (from, type, messageId) in payload.ExtractUnsupportedMessages())
        {
            try
            {
                await ProcessAsync(from, messageId, () => _engine.HandleUnsupportedMediaAsync(from, type, ct), ct);
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
