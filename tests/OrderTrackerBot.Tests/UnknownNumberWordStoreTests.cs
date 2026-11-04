using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// The unknown-number-word log is saved so the counts survive a restart. Recording must stay in memory; only the flush writes.
// The log is process-wide, so the tests that clear it share one collection and run one at a time.
[Collection("UnknownNumberWordLog")]
public class UnknownNumberWordStoreTests
{
    private static (SqliteConnection, AppDbContext) NewDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        return (connection, db);
    }

    [Fact]
    public async Task Flush_SavesCounts_AndAddsToThemNextTime()
    {
        UnknownNumberWordLog.Clear();
        var (connection, db) = NewDb();
        using var _ = connection; using var __ = db;

        UnknownNumberWordLog.Record(new[] { "ekhatarr", "ekhatarr", "Zyada" });
        Assert.Empty(db.UnknownNumberWords); // recording alone writes nothing
        await UnknownNumberWordLog.FlushAsync(db, DateTime.UtcNow, default);

        UnknownNumberWordLog.Record(new[] { "ekhatarr" });
        await UnknownNumberWordLog.FlushAsync(db, DateTime.UtcNow, default);

        // Other tests may record words too, so only this test's words are compared.
        var stored = (await UnknownNumberWordLog.StoredAsync(db, default)).Where(x => x.Word is "ekhatarr" or "zyada").ToList();
        Assert.Equal(new[] { ("ekhatarr", 3), ("zyada", 1) }, stored);
    }

    [Fact]
    public async Task Flush_DoesNotCountTheSameWordTwice()
    {
        UnknownNumberWordLog.Clear();
        var (connection, db) = NewDb();
        using var _ = connection; using var __ = db;

        UnknownNumberWordLog.Record(new[] { "dohraya" });
        await UnknownNumberWordLog.FlushAsync(db, DateTime.UtcNow, default);
        await UnknownNumberWordLog.FlushAsync(db, DateTime.UtcNow, default);

        Assert.Equal(1, (await UnknownNumberWordLog.StoredAsync(db, default)).Single(x => x.Word == "dohraya").Count);
    }
}
