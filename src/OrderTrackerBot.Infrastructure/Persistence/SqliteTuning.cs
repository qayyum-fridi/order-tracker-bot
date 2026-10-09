using Microsoft.EntityFrameworkCore;

namespace OrderTrackerBot.Infrastructure.Persistence;

public static class SqliteTuning
{
    /// <summary>
    /// Write-ahead logging lets readers and the single writer run side by side instead of blocking each other. The mode is
    /// stored in the database file, so one call is enough; it is a no-op for in-memory databases.
    /// </summary>
    public static void EnableWal(AppDbContext db) =>
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}
