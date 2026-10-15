using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>Resolves "Authorization: Bearer &lt;token&gt;" to an active seller. Fails closed: no match, no access.</summary>
public sealed class ClientAuthFilter(AppDbContext db) : IAsyncAuthorizationFilter
{
    public const string SellerIdItem = "ClientSellerId";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Result = Unauthorized();
            return;
        }

        var hash = ClientTokens.Hash(header["Bearer ".Length..].Trim());
        var sellerId = await db.Sellers.AsNoTracking()
            .Where(s => s.ClientApiTokenHash == hash && s.Status == SellerStatus.Active)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (sellerId is null)
        {
            context.Result = Unauthorized();
            return;
        }
        context.HttpContext.Items[SellerIdItem] = sellerId.Value;
    }

    private static UnauthorizedObjectResult Unauthorized() =>
        new(new { error = "Missing or invalid client token, or the account is inactive." });
}
