using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Infrastructure.Backup;

namespace OrderTrackerBot.Tests;

public class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otb-backup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FolderStorage _storage;
    private readonly string _db;

    public BackupTests()
    {
        Directory.CreateDirectory(_dir);
        _storage = new FolderStorage(Path.Combine(_dir, "drive"));
        _db = Path.Combine(_dir, "live", "app.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_db)!);
    }

    private SqliteBackupManager Manager(int keep = 40) =>
        new(_storage, Options.Create(new BackupOptions { KeepCount = keep }), NullLogger<SqliteBackupManager>.Instance);

    [Fact]
    public async Task Corrupt_database_is_replaced_by_the_newest_backup()
    {
        MakeDb(_db, "Ayesha", "Bilal");
        await Manager().BackupAsync(_db, default);

        Corrupt(_db);
        var outcome = await Manager().RestoreIfNeededAsync(_db, default);

        Assert.Equal(RestoreResult.Restored, outcome.Result);
        Assert.Equal(new[] { "Ayesha", "Bilal" }, Names(_db));
        Assert.True(File.Exists(outcome.CorruptCopy), "the damaged file is kept for inspection");
    }

    [Fact]
    public async Task Missing_database_is_restored_from_backup()
    {
        MakeDb(_db, "Sara");
        await Manager().BackupAsync(_db, default);
        File.Delete(_db);

        var outcome = await Manager().RestoreIfNeededAsync(_db, default);

        Assert.Equal(RestoreResult.Restored, outcome.Result);
        Assert.Null(outcome.CorruptCopy);
        Assert.Equal(new[] { "Sara" }, Names(_db));
    }

    [Fact]
    public async Task Fresh_install_with_no_backups_is_left_alone()
    {
        var outcome = await Manager().RestoreIfNeededAsync(_db, default);

        Assert.Equal(RestoreResult.NotNeeded, outcome.Result);
        Assert.False(File.Exists(_db));
    }

    [Fact]
    public async Task Unreadable_newest_backup_falls_back_to_the_older_one()
    {
        MakeDb(_db, "Old");
        await Manager().BackupAsync(_db, default);
        await _storage.UploadAsync(BackupOptions.FilePrefix + "99999999-999999.db.gz", new MemoryStream("not a gzip"u8.ToArray()), default);

        Corrupt(_db);
        var outcome = await Manager().RestoreIfNeededAsync(_db, default);

        Assert.Equal(RestoreResult.Restored, outcome.Result);
        Assert.Equal(new[] { "Old" }, Names(_db));
    }

    [Fact]
    public async Task Corrupt_database_with_only_bad_backups_reports_no_usable_backup_and_keeps_the_file()
    {
        await _storage.UploadAsync(BackupOptions.FilePrefix + "20260101-000000.db.gz", new MemoryStream("junk"u8.ToArray()), default);
        MakeDb(_db, "X");
        Corrupt(_db);
        var before = File.ReadAllBytes(_db);

        var outcome = await Manager().RestoreIfNeededAsync(_db, default);

        Assert.Equal(RestoreResult.NoUsableBackup, outcome.Result);
        Assert.Equal(before, File.ReadAllBytes(_db));
    }

    [Fact]
    public async Task Healthy_database_never_touches_the_storage()
    {
        MakeDb(_db, "Fine");
        _storage.Throw = true;

        var outcome = await Manager().RestoreIfNeededAsync(_db, default);

        Assert.Equal(RestoreResult.NotNeeded, outcome.Result);
    }

    [Fact]
    public async Task A_corrupt_live_database_is_never_uploaded_as_a_backup()
    {
        MakeDb(_db, "X");
        Corrupt(_db);

        await Assert.ThrowsAnyAsync<Exception>(() => Manager().BackupAsync(_db, default));

        Assert.Empty(await _storage.ListNewestFirstAsync(default));
    }

    [Fact]
    public async Task Only_the_newest_KeepCount_backups_are_kept()
    {
        MakeDb(_db, "A");
        for (var i = 1; i <= 5; i++)
            await _storage.UploadAsync($"{BackupOptions.FilePrefix}2026010{i}-000000.db.gz", new MemoryStream(new byte[] { 1 }), default);

        await Manager(keep: 3).BackupAsync(_db, default);

        var names = (await _storage.ListNewestFirstAsync(default)).Select(f => f.Name).ToList();
        Assert.Equal(3, names.Count);
        Assert.DoesNotContain(names, n => n.Contains("20260101") || n.Contains("20260102") || n.Contains("20260103"));
    }

    [Fact]
    public void Orders_have_a_seller_and_date_index()
    {
        using var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        Assert.Contains("IX_Orders_SellerId_CreatedAt", db.Database.GenerateCreateScript());
    }

    private static void MakeDb(string path, params string[] names)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE T (Name TEXT)";
        create.ExecuteNonQuery();
        foreach (var name in names)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO T VALUES ($n)";
            insert.Parameters.AddWithValue("$n", name);
            insert.ExecuteNonQuery();
        }
    }

    private static List<string> Names(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM T ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static void Corrupt(string path) => File.WriteAllBytes(path, Enumerable.Repeat((byte)0xFF, 8192).ToArray());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Stands in for Google Drive: files in a folder, "created" one minute apart in upload order.</summary>
    private sealed class FolderStorage : IBackupStorage
    {
        private readonly string _folder;
        private DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public bool Throw { get; set; }

        public FolderStorage(string folder)
        {
            _folder = folder;
            Directory.CreateDirectory(folder);
        }

        public async Task UploadAsync(string name, Stream content, CancellationToken cancellationToken)
        {
            await using (var file = File.Create(Path.Combine(_folder, name)))
                await content.CopyToAsync(file, cancellationToken);
            _clock = _clock.AddMinutes(1);
            File.SetLastWriteTimeUtc(Path.Combine(_folder, name), _clock);
        }

        public Task<IReadOnlyList<BackupFile>> ListNewestFirstAsync(CancellationToken cancellationToken)
        {
            if (Throw) throw new HttpRequestException("Drive is down");
            IReadOnlyList<BackupFile> files = new DirectoryInfo(_folder).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new BackupFile(f.Name, f.Name, f.LastWriteTimeUtc))
                .ToList();
            return Task.FromResult(files);
        }

        public async Task DownloadAsync(BackupFile file, Stream destination, CancellationToken cancellationToken)
        {
            await using var source = File.OpenRead(Path.Combine(_folder, file.Id));
            await source.CopyToAsync(destination, cancellationToken);
        }

        public Task DeleteAsync(BackupFile file, CancellationToken cancellationToken)
        {
            File.Delete(Path.Combine(_folder, file.Id));
            return Task.CompletedTask;
        }
    }
}
