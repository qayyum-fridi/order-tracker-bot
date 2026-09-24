using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Infrastructure.Instagram;

public class InstagramOptions
{
    public const string SectionName = "Instagram";

    /// <summary>
    /// "facebook" = Instagram API with Facebook Login (IG account linked to a Page; scopes instagram_manage_comments etc).
    /// "instagram" = Instagram API with Instagram Login (no Page needed; scopes instagram_business_*).
    /// Use the one your Meta App Review approval is for.
    /// </summary>
    public string LoginMode { get; set; } = "facebook";
    /// <summary>Facebook App ID (facebook mode) or Instagram App ID (instagram mode).</summary>
    public string AppId { get; set; } = "";
    /// <summary>Matching app secret; also verifies the X-Hub-Signature-256 on comment webhooks.</summary>
    public string AppSecret { get; set; } = "";
    /// <summary>Public https base URL of this API, e.g. https://bot.example.pk — used for the connect link and OAuth redirect.</summary>
    public string PublicBaseUrl { get; set; } = "";
    /// <summary>Webhook verify token for the Instagram webhook (falls back to WhatsApp:VerifyToken when empty).</summary>
    public string VerifyToken { get; set; } = "";
    public string GraphVersion { get; set; } = "v21.0";

    public bool IsFacebookLogin => LoginMode.Equals("facebook", StringComparison.OrdinalIgnoreCase);
    public string RedirectUri => PublicBaseUrl.TrimEnd('/') + "/instagram/callback";
}

public sealed record InstagramComment(string IgAccountId, string CommentId, string? FromId, string? Username, string Text, string? MediaId);

/// <summary>Reads comment events out of an "instagram" object webhook (same shape for both login flavours).</summary>
public static class InstagramWebhookPayload
{
    public static IReadOnlyList<InstagramComment> ExtractComments(string json)
    {
        var comments = new List<InstagramComment>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return comments; }

        if (root?["object"]?.GetValue<string>() is not "instagram") return comments;
        foreach (var entry in root["entry"] as JsonArray ?? new JsonArray())
        {
            var accountId = InstagramClient.JsonId(entry?["id"]);
            if (accountId is null) continue;
            foreach (var change in entry!["changes"] as JsonArray ?? new JsonArray())
            {
                if (change?["field"]?.GetValue<string>() is not ("comments" or "live_comments")) continue;
                var value = change["value"];
                var commentId = InstagramClient.JsonId(value?["id"]);
                var text = value?["text"]?.GetValue<string>();
                if (commentId is null || string.IsNullOrWhiteSpace(text)) continue;
                comments.Add(new InstagramComment(accountId, commentId, InstagramClient.JsonId(value!["from"]?["id"]),
                    value["from"]?["username"]?.GetValue<string>(), text, InstagramClient.JsonId(value["media"]?["id"])));
            }
        }
        return comments;
    }
}

/// <summary>Result of a completed OAuth connect: the IG professional account and the token to act on its comments with.</summary>
public sealed record InstagramAccount(string IgUserId, string? Username, string? PageId, string AccessToken, DateTime? ExpiresAt);

/// <summary>
/// Instagram comments over the Graph API (section 6c). Both login flavours are supported because Meta approves
/// the comment permission per flavour: Facebook Login (graph.facebook.com via the linked Page's never-expiring token)
/// and Instagram Login (graph.instagram.com, user token refreshed every ~60 days).
/// </summary>
public class InstagramClient : IInstagramClient
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly InstagramOptions _options;
    private readonly ILogger<InstagramClient> _logger;

    public InstagramClient(HttpClient http, IOptions<InstagramOptions> options, ILogger<InstagramClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.AppId) && !string.IsNullOrWhiteSpace(_options.AppSecret) && !string.IsNullOrWhiteSpace(_options.PublicBaseUrl);

    private string GraphBase => _options.IsFacebookLogin
        ? $"https://graph.facebook.com/{_options.GraphVersion}"
        : $"https://graph.instagram.com/{_options.GraphVersion}";

    // ------------------------------------------------------------ connect link / OAuth state

    public string BuildConnectLink(int sellerId) => $"{_options.PublicBaseUrl.TrimEnd('/')}/instagram/connect/{CreateState(sellerId, DateTime.UtcNow)}";

    /// <summary>"sellerId.expiryUnix.signature" — stops one seller from connecting an account to another seller's chat.</summary>
    public string CreateState(int sellerId, DateTime utcNow)
    {
        var payload = $"{sellerId}.{new DateTimeOffset(utcNow + StateLifetime).ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload)}";
    }

    public bool TryReadState(string? state, DateTime utcNow, out int sellerId)
    {
        sellerId = 0;
        var parts = (state ?? "").Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var id) || !long.TryParse(parts[1], out var expiry)) return false;
        var expected = Encoding.ASCII.GetBytes(Sign($"{parts[0]}.{parts[1]}"));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[2]))) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) < utcNow) return false;
        sellerId = id;
        return true;
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.AppSecret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)), 0, 18)
            .Replace('+', '-').Replace('/', '_');
    }

    public string BuildAuthorizeUrl(string state)
    {
        var redirect = Uri.EscapeDataString(_options.RedirectUri);
        return _options.IsFacebookLogin
            ? $"https://www.facebook.com/{_options.GraphVersion}/dialog/oauth?client_id={_options.AppId}&redirect_uri={redirect}&state={state}" +
              "&scope=instagram_basic,instagram_manage_comments,pages_show_list,pages_read_engagement,pages_manage_metadata,business_management"
            : $"https://www.instagram.com/oauth/authorize?client_id={_options.AppId}&redirect_uri={redirect}&response_type=code&state={state}" +
              "&scope=instagram_business_basic,instagram_business_manage_comments";
    }

    // ------------------------------------------------------------ code exchange

    /// <summary>Exchanges the OAuth code for a long-lived token and finds the IG professional account. Throws with a readable message on failure.</summary>
    public Task<InstagramAccount> ExchangeCodeAsync(string code, CancellationToken ct) =>
        _options.IsFacebookLogin ? ExchangeFacebookAsync(code, ct) : ExchangeInstagramAsync(code, ct);

    private async Task<InstagramAccount> ExchangeInstagramAsync(string code, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.AppId,
            ["client_secret"] = _options.AppSecret,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = _options.RedirectUri,
            ["code"] = code
        });
        var shortLived = await SendAsync(HttpMethod.Post, "https://api.instagram.com/oauth/access_token", form, ct);
        var data = shortLived["data"] is JsonArray { Count: > 0 } arr ? arr[0]! : shortLived;
        var shortToken = data["access_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Instagram did not return an access token.");

        var longLived = await SendAsync(HttpMethod.Get,
            $"https://graph.instagram.com/access_token?grant_type=ig_exchange_token&client_secret={Uri.EscapeDataString(_options.AppSecret)}&access_token={Uri.EscapeDataString(shortToken)}", null, ct);
        var token = longLived["access_token"]?.GetValue<string>() ?? shortToken;
        var expiresIn = longLived["expires_in"]?.GetValue<long>();

        var me = await SendAsync(HttpMethod.Get, $"{GraphBase}/me?fields=user_id,username&access_token={Uri.EscapeDataString(token)}", null, ct);
        var igUserId = JsonId(me["user_id"]) ?? JsonId(me["id"]) ?? throw new InvalidOperationException("Instagram account id not returned.");
        return new InstagramAccount(igUserId, me["username"]?.GetValue<string>(), null, token,
            expiresIn is { } secs ? DateTime.UtcNow.AddSeconds(secs) : DateTime.UtcNow.AddDays(60));
    }

    private async Task<InstagramAccount> ExchangeFacebookAsync(string code, CancellationToken ct)
    {
        var app = $"client_id={_options.AppId}&client_secret={Uri.EscapeDataString(_options.AppSecret)}";
        var shortLived = await SendAsync(HttpMethod.Get,
            $"{GraphBase}/oauth/access_token?{app}&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}&code={Uri.EscapeDataString(code)}", null, ct);
        var shortToken = shortLived["access_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Facebook did not return an access token.");

        var longLived = await SendAsync(HttpMethod.Get,
            $"{GraphBase}/oauth/access_token?grant_type=fb_exchange_token&{app}&fb_exchange_token={Uri.EscapeDataString(shortToken)}", null, ct);
        var userToken = longLived["access_token"]?.GetValue<string>() ?? shortToken;

        var pages = await SendAsync(HttpMethod.Get,
            $"{GraphBase}/me/accounts?fields=id,name,access_token,instagram_business_account{{id,username}}&access_token={Uri.EscapeDataString(userToken)}", null, ct);
        var page = (pages["data"] as JsonArray)?.FirstOrDefault(p => p?["instagram_business_account"] is JsonObject)
                   ?? throw new InvalidOperationException(
                       "Koi Facebook Page nahi mila jis se Instagram Business account linked ho. IG ko Business/Creator bana kar Page se link karein.");
        var ig = page["instagram_business_account"]!;
        // A Page token obtained from a long-lived user token does not expire.
        return new InstagramAccount(JsonId(ig["id"])!, ig["username"]?.GetValue<string>(), JsonId(page["id"]),
            page["access_token"]?.GetValue<string>() ?? userToken, null);
    }

    /// <summary>Turns on comment webhooks for this account (Facebook Login: install the app on the Page; Instagram Login: per-account subscription).</summary>
    public async Task SubscribeToCommentsAsync(InstagramAccount account, CancellationToken ct)
    {
        var url = _options.IsFacebookLogin
            ? $"{GraphBase}/{account.PageId}/subscribed_apps?subscribed_fields=feed&access_token={Uri.EscapeDataString(account.AccessToken)}"
            : $"{GraphBase}/me/subscribed_apps?subscribed_fields=comments&access_token={Uri.EscapeDataString(account.AccessToken)}";
        await SendAsync(HttpMethod.Post, url, null, ct);
    }

    // ------------------------------------------------------------ replies + refresh

    public async Task<bool> ReplyToCommentAsync(InstagramConnection connection, string commentId, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["message"] = message,
                ["access_token"] = connection.AccessToken
            });
            await SendAsync(HttpMethod.Post, $"{GraphBase}/{Uri.EscapeDataString(commentId)}/replies", form, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instagram comment reply failed for seller {SellerId}", connection.SellerId);
            return false;
        }
    }

    /// <summary>Instagram Login tokens last ~60 days; refresh any expiring within a week (must be at least 24h old).</summary>
    public async Task RefreshExpiringTokensAsync(IAppDbContext db, DateTime utcNow, CancellationToken ct)
    {
        if (!IsConfigured || _options.IsFacebookLogin) return;

        var soon = utcNow.AddDays(7);
        var dayAgo = utcNow.AddDays(-1);
        var due = await db.InstagramConnections
            .Where(c => c.TokenExpiresAt != null && c.TokenExpiresAt < soon && c.ConnectedAt < dayAgo)
            .ToListAsync(ct);
        foreach (var connection in due)
        {
            try
            {
                var refreshed = await SendAsync(HttpMethod.Get,
                    $"https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token&access_token={Uri.EscapeDataString(connection.AccessToken)}", null, ct);
                if (refreshed["access_token"]?.GetValue<string>() is { } token) connection.AccessToken = token;
                connection.TokenExpiresAt = utcNow.AddSeconds(refreshed["expires_in"]?.GetValue<long>() ?? 60L * 24 * 3600);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Instagram token refresh failed for seller {SellerId}; they will need to reconnect.", connection.SellerId);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string message = body;
            try
            {
                var error = JsonNode.Parse(body)?["error"];
                message = error?["error_user_msg"]?.GetValue<string>() ?? error?["message"]?.GetValue<string>() ?? body;
            }
            catch (System.Text.Json.JsonException) { }
            throw new InvalidOperationException($"Meta API {(int)response.StatusCode}: {message}");
        }
        return JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) ?? new JsonObject();
    }

    /// <summary>Meta returns ids as strings or numbers depending on the endpoint.</summary>
    internal static string? JsonId(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        _ => node.ToJsonString()
    };
}
