namespace OrderTrackerBot.Api.Admin;

/// <summary>
/// Settings for the admin API under <c>/api/admin</c>. The admin panel (Next.js) holds the admin login
/// and sends <see cref="ApiKey"/> on every call; the key never reaches a browser.
/// </summary>
public class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Shared secret sent in the X-Admin-Api-Key header. Empty = admin API refuses every call (503).</summary>
    public string ApiKey { get; set; } = "";
}
