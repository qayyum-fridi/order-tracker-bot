using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Instagram;
using OrderTrackerBot.Infrastructure.Persistence;
using OrderTrackerBot.Infrastructure.WhatsApp;

namespace OrderTrackerBot.Api.Controllers;

/// <summary>Section 6c: seller OAuth connect (screen 5d-5) and the comment webhook (screens 5d-6 / 5d-10).</summary>
[ApiController]
public class InstagramController : ControllerBase
{
    private readonly InstagramClient _instagram;
    private readonly InstagramOptions _options;
    private readonly WhatsAppOptions _whatsApp;
    private readonly AppDbContext _db;
    private readonly ConversationEngine _engine;
    private readonly ILogger<InstagramController> _logger;

    public InstagramController(InstagramClient instagram, IOptions<InstagramOptions> options, IOptions<WhatsAppOptions> whatsApp,
        AppDbContext db, ConversationEngine engine, ILogger<InstagramController> logger)
    {
        _instagram = instagram;
        _options = options.Value;
        _whatsApp = whatsApp.Value;
        _db = db;
        _engine = engine;
        _logger = logger;
    }

    /// <summary>The short link sent in chat; checks the signed state, then hands off to Meta's login dialog.</summary>
    [HttpGet("instagram/connect/{state}")]
    public IActionResult Connect(string state)
    {
        if (!_instagram.IsConfigured) return Page("Instagram connect abhi active nahi hai.", ok: false);
        if (!_instagram.TryReadState(state, DateTime.UtcNow, out _))
            return Page("Yeh link expire ho gaya hai. WhatsApp par dobara \"connect instagram\" likhein.", ok: false);
        return Redirect(_instagram.BuildAuthorizeUrl(state));
    }

    [HttpGet("instagram/callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state,
        [FromQuery(Name = "error_description")] string? errorDescription, CancellationToken ct)
    {
        if (!_instagram.TryReadState(state, DateTime.UtcNow, out var sellerId))
            return Page("Yeh link expire ho gaya hai. WhatsApp par dobara \"connect instagram\" likhein.", ok: false);
        if (string.IsNullOrEmpty(code))
            return Page($"Permission nahi mili{(string.IsNullOrEmpty(errorDescription) ? "" : $" ({errorDescription})")}. Dobara try karein.", ok: false);

        InstagramAccount account;
        try
        {
            account = await _instagram.ExchangeCodeAsync(code, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instagram OAuth exchange failed for seller {SellerId}", sellerId);
            return Page($"Connect nahi ho saka: {ex.Message}", ok: false);
        }

        // One IG account belongs to one seller chat: a reconnect (or a different seller claiming it) replaces the old row.
        var stale = await _db.InstagramConnections.Where(c => c.SellerId == sellerId || c.IgUserId == account.IgUserId).ToListAsync(ct);
        _db.InstagramConnections.RemoveRange(stale);
        _db.InstagramConnections.Add(new InstagramConnection
        {
            SellerId = sellerId,
            IgUserId = account.IgUserId,
            Username = account.Username,
            PageId = account.PageId,
            AccessToken = account.AccessToken,
            TokenExpiresAt = account.ExpiresAt
        });
        await _db.SaveChangesAsync(ct);

        try
        {
            await _instagram.SubscribeToCommentsAsync(account, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instagram webhook subscription failed for seller {SellerId}", sellerId);
        }

        await _engine.HandleInstagramConnectedAsync(sellerId, account.Username, ct);
        return Page($"✅ Instagram connect ho gaya — @{account.Username}. Ab WhatsApp par wapas jayein.", ok: true);
    }

    [HttpGet("webhook/instagram")]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var expected = string.IsNullOrEmpty(_options.VerifyToken) ? _whatsApp.VerifyToken : _options.VerifyToken;
        if (mode == "subscribe" && !string.IsNullOrEmpty(expected) && verifyToken == expected && challenge is not null)
            return Content(challenge, "text/plain");
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    [HttpPost("webhook/instagram")]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
        if (!WhatsAppSignatureValidator.IsValid(_options.AppSecret, System.Text.Encoding.UTF8.GetBytes(rawBody), signature))
        {
            _logger.LogWarning("Rejected Instagram webhook call with invalid signature.");
            return Unauthorized();
        }

        foreach (var comment in InstagramWebhookPayload.ExtractComments(rawBody))
        {
            try
            {
                await _engine.HandleInstagramCommentAsync(comment.IgAccountId, comment.CommentId, comment.FromId, comment.Username,
                    comment.Text, comment.MediaId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Instagram comment {CommentId}", comment.CommentId);
            }
        }

        // Meta retries (and eventually disables the subscription) on anything but a fast 200.
        return Ok();
    }

    private ContentResult Page(string message, bool ok) => Content(
        "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>Order Assistant</title></head><body style=\"font-family:system-ui,sans-serif;max-width:480px;margin:15vh auto;padding:0 16px;text-align:center\">" +
        $"<p style=\"font-size:48px;margin:0\">{(ok ? "✅" : "⚠️")}</p><p style=\"font-size:18px\">{WebUtility.HtmlEncode(message)}</p></body></html>",
        "text/html; charset=utf-8");
}
