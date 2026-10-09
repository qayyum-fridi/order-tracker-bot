namespace OrderTrackerBot.Infrastructure.Backup;

/// <summary>
/// Automatic Sqlite backups to Google Drive (section "Backup"). Off by default so local dev needs no setup.
/// Auth is an OAuth refresh token for the Drive owner with the narrow <c>drive.file</c> scope, so the bot can only
/// see the backup files it created itself. See README "Backups".
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";
    public const string FilePrefix = "ordertrackerbot-";

    public bool Enabled { get; set; }

    /// <summary>A new backup is taken when the newest one on Drive is at least this old. Also the most data a restore can lose.</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>Newest backups kept on Drive; older ones are deleted after each successful upload.</summary>
    public int KeepCount { get; set; } = 40;

    /// <summary>Drive folder id (the last part of the folder's URL).</summary>
    public string FolderId { get; set; } = "";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(FolderId) && !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RefreshToken);
}
