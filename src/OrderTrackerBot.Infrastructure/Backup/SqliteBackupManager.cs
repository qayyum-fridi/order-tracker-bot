using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderTrackerBot.Infrastructure.Backup;

public enum RestoreResult { NotNeeded, Restored, NoUsableBackup }

/// <param name="BackupName">The backup that was restored (when <see cref="RestoreResult.Restored"/>).</param>
/// <param name="CorruptCopy">Where the damaged file was kept for inspection, if there was one.</param>
public sealed record RestoreOutcome(RestoreResult Result, string? BackupName = null, string? CorruptCopy = null);

/// <summary>Snapshots the Sqlite file to a gzip on <see cref="IBackupStorage"/>, and restores the newest healthy snapshot when the live file is corrupt or gone.</summary>
public sealed class SqliteBackupManager
{
    private readonly IBackupStorage _storage;
    private readonly BackupOptions _options;
    private readonly ILogger<SqliteBackupManager> _logger;

    public SqliteBackupManager(IBackupStorage storage, IOptions<BackupOptions> options, ILogger<SqliteBackupManager> logger)
    {
        _storage = storage;
        _options = options.Value;
        _logger = logger;
    }

    public static string ResolveDbPath(string connectionString) =>
        Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource);

    /// <summary>True when the file opens as a Sqlite database and <c>PRAGMA integrity_check</c> says "ok".</summary>
    public static bool IsHealthy(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
            using var connection = Open(path);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            return string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<DateTime?> LastBackupUtcAsync(CancellationToken cancellationToken)
    {
        var files = await _storage.ListNewestFirstAsync(cancellationToken);
        return files.Count == 0 ? null : files[0].CreatedUtc;
    }

    /// <summary>Takes a consistent snapshot (safe while the app is writing), verifies it, uploads it, then prunes old ones. A corrupt snapshot is never uploaded.</summary>
    public async Task<string> BackupAsync(string dbPath, CancellationToken cancellationToken)
    {
        var name = $"{BackupOptions.FilePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}.db.gz";
        var snapshot = Path.Combine(Path.GetTempPath(), $"otb-backup-{Guid.NewGuid():N}.db");
        var gzip = snapshot + ".gz";
        try
        {
            using (var source = Open(dbPath))
            using (var copy = Open(snapshot))
            {
                source.Open();
                copy.Open();
                source.BackupDatabase(copy);
            }

            if (!IsHealthy(snapshot))
                throw new InvalidOperationException("The live database failed its integrity check, so no backup was uploaded.");

            using (var input = File.OpenRead(snapshot))
            using (var output = File.Create(gzip))
            using (var compressor = new GZipStream(output, CompressionLevel.Optimal))
                await input.CopyToAsync(compressor, cancellationToken);

            await using (var upload = File.OpenRead(gzip))
                await _storage.UploadAsync(name, upload, cancellationToken);
        }
        finally
        {
            TryDelete(snapshot);
            TryDelete(gzip);
        }

        await PruneAsync(cancellationToken);
        _logger.LogInformation("Database backup uploaded: {Name}", name);
        return name;
    }

    /// <summary>
    /// Healthy file -> nothing. Corrupt or missing file -> the newest backup that downloads, unzips and passes its own
    /// integrity check replaces it (the damaged file is kept beside it as <c>.corrupt-*</c>). A missing file with no backups is a fresh install.
    /// </summary>
    public async Task<RestoreOutcome> RestoreIfNeededAsync(string dbPath, CancellationToken cancellationToken)
    {
        var exists = File.Exists(dbPath) && new FileInfo(dbPath).Length > 0;
        if (exists && IsHealthy(dbPath)) return new RestoreOutcome(RestoreResult.NotNeeded);

        var backups = await _storage.ListNewestFirstAsync(cancellationToken);
        if (!exists && backups.Count == 0) return new RestoreOutcome(RestoreResult.NotNeeded);

        _logger.LogError("Database {Path} is {State}; trying {Count} backup(s), newest first", dbPath, exists ? "corrupt" : "missing", backups.Count);

        var directory = Path.GetDirectoryName(dbPath)!;
        foreach (var backup in backups)
        {
            var downloaded = Path.Combine(directory, $".restore-{Guid.NewGuid():N}.gz");
            var restored = Path.Combine(directory, $".restore-{Guid.NewGuid():N}.db");
            try
            {
                await using (var file = File.Create(downloaded))
                    await _storage.DownloadAsync(backup, file, cancellationToken);

                using (var input = File.OpenRead(downloaded))
                using (var decompressor = new GZipStream(input, CompressionMode.Decompress))
                using (var output = File.Create(restored))
                    await decompressor.CopyToAsync(output, cancellationToken);

                if (!IsHealthy(restored))
                {
                    _logger.LogWarning("Backup {Name} failed its integrity check; trying the next older one", backup.Name);
                    continue;
                }

                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                string? corruptCopy = null;
                if (File.Exists(dbPath))
                {
                    corruptCopy = $"{dbPath}.corrupt-{stamp}";
                    File.Move(dbPath, corruptCopy);
                }
                foreach (var side in new[] { "-wal", "-shm" })
                    if (File.Exists(dbPath + side))
                        File.Move(dbPath + side, $"{dbPath}.corrupt-{stamp}{side}");

                File.Move(restored, dbPath);
                _logger.LogError("Database restored from backup {Name}", backup.Name);
                return new RestoreOutcome(RestoreResult.Restored, backup.Name, corruptCopy);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not restore from backup {Name}; trying the next older one", backup.Name);
            }
            finally
            {
                TryDelete(downloaded);
                TryDelete(restored);
            }
        }

        return new RestoreOutcome(RestoreResult.NoUsableBackup);
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var files = await _storage.ListNewestFirstAsync(cancellationToken);
            foreach (var old in files.Skip(Math.Max(1, _options.KeepCount)))
                await _storage.DeleteAsync(old, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pruning old backups failed (the new backup itself was uploaded)");
        }
    }

    private static SqliteConnection Open(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
