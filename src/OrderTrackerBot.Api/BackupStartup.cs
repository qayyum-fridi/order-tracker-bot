using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.Backup;

namespace OrderTrackerBot.Api;

public static class BackupStartup
{
    /// <summary>
    /// Runs before the database is opened. If the Sqlite file is corrupt or missing, the newest healthy Drive backup replaces it
    /// and the founder is alerted. A healthy file never touches Drive, so a Drive outage cannot stop the bot from starting.
    /// </summary>
    public static async Task RestoreIfNeededAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BackupStartup");
        var options = app.Configuration.GetSection(BackupOptions.SectionName).Get<BackupOptions>() ?? new BackupOptions();
        if (options.Enabled && !options.IsConfigured)
            logger.LogWarning("Backup:Enabled is true but Backup:FolderId/ClientId/ClientSecret/RefreshToken are not all set; backups and auto-restore are OFF.");

        var manager = app.Services.GetService<SqliteBackupManager>();
        if (manager is null) return;

        var path = SqliteBackupManager.ResolveDbPath(app.Configuration.GetConnectionString("Default")!);
        var reporter = app.Services.GetRequiredService<IIssueReporter>();
        try
        {
            var outcome = await manager.RestoreIfNeededAsync(path, CancellationToken.None);
            switch (outcome.Result)
            {
                case RestoreResult.Restored:
                    await reporter.ReportAsync(IssueCodes.DatabaseRestored, null,
                        $"Restored from {outcome.BackupName}. Damaged file kept as {outcome.CorruptCopy ?? "(none, file was missing)"}. Data after that backup is lost.");
                    break;
                case RestoreResult.NoUsableBackup:
                    await reporter.ReportAsync(IssueCodes.DatabaseRestoreFailed, null, $"{path} is corrupt/missing and no Drive backup passed its integrity check.");
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database restore check failed");
            await reporter.ReportAsync(IssueCodes.DatabaseRestoreFailed, null, "Could not check/restore the database from Drive.", ex);
        }
    }
}
