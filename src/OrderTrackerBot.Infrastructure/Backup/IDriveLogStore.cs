namespace OrderTrackerBot.Infrastructure.Backup;

/// <summary>Keeps a growing local file mirrored on Drive: the same name in the same subfolder is replaced, not duplicated.</summary>
public interface IDriveLogStore
{
    Task UploadOrReplaceAsync(string folderName, string fileName, string localPath, CancellationToken cancellationToken);
}
