using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Application.Auth;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Controllers;

/// <summary>
/// Phone login for the client dashboard. Anonymous on purpose: the one-time code that the bot sends to the seller's
/// WhatsApp is the credential. Failures all return the same answer, so the endpoint doesn't reveal which numbers exist.
/// </summary>
[ApiController]
[Route("api/client/auth")]
public class ClientAuthController(AppDbContext db) : ControllerBase
{
    public sealed record VerifyCodeRequest(string? Phone, string? Code);

    /// <summary>Exchanges the WhatsApp code for a dashboard token. Issuing a token replaces the seller's previous one.</summary>
    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] VerifyCodeRequest request, CancellationToken ct)
    {
        var subscriber = ClientLoginCodes.SubscriberDigits(request.Phone);
        var code = request.Code?.Trim();
        if (subscriber is null || string.IsNullOrEmpty(code)) return Invalid();

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
        return Ok(new { token, businessName = seller.BusinessName });
    }

    private IActionResult Invalid() =>
        Unauthorized(new { error = "Code is wrong or expired. Send \"login\" on WhatsApp to get a new one." });
}
