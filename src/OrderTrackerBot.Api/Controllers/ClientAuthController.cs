using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Auth;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Controllers;

/// <summary>
/// Phone login for the client dashboard. Anonymous on purpose: the one-time code that the bot sends to the seller's
/// WhatsApp is the credential. Failures all return the same answer, so the endpoint doesn't reveal which numbers exist.
/// </summary>
[ApiController]
[Route("api/client/auth")]
public class ClientAuthController(AppDbContext db, ClientLoginLimiter? limiter = null, IWhatsAppSender? sender = null) : ControllerBase
{
    public sealed record VerifyCodeRequest(string? Phone, string? Code);

    /// <summary>Exchanges the WhatsApp code for a dashboard token. Issuing a token replaces the seller's previous one.</summary>
    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyCodeRequest request, CancellationToken ct)
    {
        var subscriber = ClientLoginCodes.SubscriberDigits(request.Phone);
        var code = request.Code?.Trim();
        if (subscriber is null || string.IsNullOrEmpty(code)) return Invalid();

        if (limiter is not null && !limiter.TryAcquire(subscriber))
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many attempts. Wait a few minutes and try again." });

        // Numbers are stored in WhatsApp's format (92XXXXXXXXXX), so the last 10 digits identify the seller.
        var candidates = await db.Sellers
            .Where(s => s.WhatsAppPhoneNumber.EndsWith(subscriber) && s.Status == SellerStatus.Active)
            .ToListAsync(ct);
        if (candidates.Count != 1) return Invalid();
        var seller = candidates[0];

        if (seller.ClientLoginCodeHash is null
            || seller.ClientLoginCodeExpiresAt < DateTime.UtcNow
            || seller.ClientLoginAttempts >= ClientLoginCodes.MaxAttempts)
            return Invalid();

        var typed = Encoding.UTF8.GetBytes(ClientLoginCodes.Hash(seller.Id, code));
        if (!CryptographicOperations.FixedTimeEquals(typed, Encoding.UTF8.GetBytes(seller.ClientLoginCodeHash)))
        {
            seller.ClientLoginAttempts++;
            await db.SaveChangesAsync(ct);
            return Invalid();
        }

        var token = ClientTokens.Create();
        seller.ClientApiTokenHash = ClientTokens.Hash(token);
        seller.ClientLoginCodeHash = null;
        seller.ClientLoginCodeExpiresAt = null;
        seller.ClientLoginAttempts = 0;
        await db.SaveChangesAsync(ct);

        // Tell the seller on WhatsApp, so a login they didn't make is noticed and can be ended with "dashboard logout".
        // The login has already succeeded, so a failed notice must not undo it.
        if (sender is not null)
        {
            try
            {
                await sender.SendTextMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("clientlogin.signedin"), ct);
            }
            catch (Exception)
            {
                // Ignored on purpose (see above).
            }
        }

        return Ok(new { token, businessName = seller.BusinessName });
    }

    /// <summary>Ends the dashboard session: the token stops working, and the dashboard clears its cookie.</summary>
    [HttpPost("logout")]
    [ServiceFilter(typeof(ClientAuthFilter))]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var sellerId = (int)HttpContext.Items[ClientAuthFilter.SellerIdItem]!;
        var seller = await db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is not null)
        {
            seller.ClientApiTokenHash = null;
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    private IActionResult Invalid() =>
        Unauthorized(new { error = "Code is wrong or expired. Send \"login\" on WhatsApp to get a new one." });
}
