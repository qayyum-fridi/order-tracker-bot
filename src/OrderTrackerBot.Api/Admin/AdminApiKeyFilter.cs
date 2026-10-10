using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace OrderTrackerBot.Api.Admin;

/// <summary>Guards the admin API: the caller must send the configured key in the X-Admin-Api-Key header.</summary>
public sealed class AdminApiKeyFilter : IAsyncAuthorizationFilter
{
    public const string HeaderName = "X-Admin-Api-Key";

    private readonly string _apiKey;

    public AdminApiKeyFilter(IOptions<AdminOptions> options)
    {
        _apiKey = options.Value.ApiKey ?? "";
    }

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Fail closed: an unconfigured key disables the whole admin API rather than leaving it open.
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            context.Result = new ObjectResult(new { error = "Admin API is disabled: set Admin:ApiKey." }) { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return Task.CompletedTask;
        }

        var given = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(_apiKey)))
            context.Result = new UnauthorizedObjectResult(new { error = "Missing or invalid admin API key." });

        return Task.CompletedTask;
    }
}
