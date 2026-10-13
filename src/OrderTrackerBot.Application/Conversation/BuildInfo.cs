using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Which build is running, for the "version" command. Set at deploy time (<c>Build:Version</c> / env <c>Build__Version</c>, e.g. the git commit SHA passed as the
/// Docker build argument GIT_SHA) so it names the deployed artifact, never a checkout. Missing or unusable = "unknown".
/// </summary>
public sealed class BuildInfo
{
    public const string SectionName = "Build";
    public const string Unknown = "unknown";

    private static readonly Regex Safe = new(@"^[A-Za-z0-9._+\-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private string? _version;

    /// <summary>A git SHA is shortened to 7 characters; anything that is not a plain token reads as "unknown".</summary>
    public string? Version
    {
        get => _version;
        set => _version = Normalize(value);
    }

    /// <summary>When this process started (UTC), so a restart after a deploy is visible too.</summary>
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public static string Normalize(string? value)
    {
        var v = (value ?? "").Trim();
        if (!Safe.IsMatch(v) || v.Equals(Unknown, StringComparison.OrdinalIgnoreCase)) return Unknown;
        return v.Length >= 12 && Regex.IsMatch(v, "^[0-9a-fA-F]+$") ? v[..7].ToLowerInvariant() : v;
    }

    public string Describe() =>
        $"ℹ️ Version: {_version ?? Unknown}\nServer shuru hua: {StartedAtUtc:dd MMM yyyy, HH:mm} UTC" +
        (_version is null or Unknown ? "\n(Build ke waqt version set nahi hua.)" : "");
}
