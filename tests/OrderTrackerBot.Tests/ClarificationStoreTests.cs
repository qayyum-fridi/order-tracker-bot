using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Stage 6 persistence: a pending action survives a save and reload, and a lookup never returns another seller's or user's question.
public class ClarificationStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ClarificationStore _store;

    public ClarificationStoreTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _store = new ClarificationStore(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // The engine stages the record and saves once at the end of the message; the tests do the same.
    private async Task SaveAsync(PendingAction pending, CancellationToken ct)
    {
        await _store.TrackAsync(pending, ct);
        await _db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task Track_DoesNotCommitOnItsOwn()
    {
        await _store.TrackAsync(Pending("a1", 1, "923001111111", T0), default);
        Assert.Empty(await _db.PendingActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FindById_IsScopedToSellerAndUser_AndCancelWaiting_ClosesOlderQuestions()
    {
        await SaveAsync(Pending("a1", 1, "923001111111", T0), default);

        Assert.NotNull(await _store.FindAsync("a1", 1, "923001111111", default));
        Assert.Null(await _store.FindAsync("a1", 2, "923001111111", default));
        Assert.Null(await _store.FindAsync("a1", 1, "923009999999", default));

        await _store.CancelWaitingAsync(1, "923001111111", default);
        await _db.SaveChangesAsync();
        Assert.Equal(PendingStatus.Cancelled, (await _store.FindAsync("a1", 1, "923001111111", default))!.Status);
    }

    private static PendingAction Pending(string id, int seller, string user, DateTime created) =>
        ClarificationManager.Create(id, seller, user, "create_order", new[] { "quantity", "status" }, "Barae meherbani quantity batayein.", created);

    [Fact]
    public async Task SaveAndReload_KeepsFieldsAnswersAndExpiry()
    {
        var p = Pending("a1", 1, "923001111111", T0);
        p.ResolvedReferences["customer"] = 42;
        ClarificationManager.Answer(p, 1, "923001111111", "2", T0, (_, reply) => reply);
        await SaveAsync(p, default);

        var loaded = await _store.FindWaitingAsync(1, "923001111111", T0.AddMinutes(1), default);

        Assert.NotNull(loaded);
        Assert.Equal("2", loaded!.Answers["quantity"]);
        Assert.Equal(new[] { "status" }, loaded.MissingFields);
        Assert.Equal(42, loaded.ResolvedReferences["customer"]);
        Assert.Equal(p.ExpiresAt, loaded.ExpiresAt);
        Assert.Equal(PendingStatus.Waiting, loaded.Status);
    }

    [Fact]
    public async Task Lookup_IsScopedToSellerAndUser()
    {
        await SaveAsync(Pending("a1", 1, "923001111111", T0), default);

        Assert.Null(await _store.FindWaitingAsync(2, "923001111111", T0, default));
        Assert.Null(await _store.FindWaitingAsync(1, "923009999999", T0, default));
    }

    [Fact]
    public async Task Lookup_IgnoresExpiredOrClosedRecords()
    {
        var expired = Pending("a1", 1, "923001111111", T0);
        await SaveAsync(expired, default);
        Assert.Null(await _store.FindWaitingAsync(1, "923001111111", T0 + ClarificationManager.DefaultTtl, default));

        var done = Pending("a2", 1, "923001111111", T0.AddMinutes(1));
        ClarificationManager.Complete(done);
        await SaveAsync(done, default);
        // "a1" is still waiting and unexpired, so it is the one returned; the completed "a2" is never returned.
        Assert.Equal("a1", (await _store.FindWaitingAsync(1, "923001111111", T0.AddMinutes(2), default))!.Id);

        ClarificationManager.Complete(expired);
        await SaveAsync(expired, default);
        Assert.Null(await _store.FindWaitingAsync(1, "923001111111", T0.AddMinutes(2), default));
    }

    [Fact]
    public async Task Save_TwiceForSameId_UpdatesInsteadOfDuplicating()
    {
        var p = Pending("a1", 1, "923001111111", T0);
        await SaveAsync(p, default);
        ClarificationManager.Answer(p, 1, "923001111111", "2", T0, (_, reply) => reply);
        await SaveAsync(p, default);

        Assert.Single(await _db.PendingActions.ToListAsync());
    }
}
