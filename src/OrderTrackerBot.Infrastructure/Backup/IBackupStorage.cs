namespace OrderTrackerBot.Infrastructure.Backup;

public sealed record BackupFile(string Id, string Name, DateTime CreatedUtc);

/// <summary>Where backup files live (Google Drive in production, a folder in tests).</summary>
public interface IBackupStorage
{
    Task UploadAsync(string name, Stream content, CancellationToken cancellationToken);

    /// <summary>Backup files only, newest first.</summary>
    Task<IReadOnlyList<BackupFile>> ListNewestFirstAsync(CancellationToken cancellationToken);

    Task DownloadAsync(BackupFile file, Stream destination, CancellationToken cancellationToken);

    Task DeleteAsync(BackupFile file, CancellationToken cancellationToken);
}
