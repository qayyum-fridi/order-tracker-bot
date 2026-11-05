using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Message wording in the database: seeded from the build, applied at runtime, and the shipped text is the fallback.</summary>
public class TemplateStoreTests : IDisposable
{
    private const string RuntimeKey = "runtime.test.001";
    private readonly TestDbContextFactory _dbFactory = new();

    public TemplateStoreTests() => TemplateStore.Reset();

    public void Dispose()
    {
        TemplateStore.Reset();
        _dbFactory.Dispose();
    }

    [Fact]
    public async Task Sync_SeedsEveryShippedRomanUrduMessageIntoTheDatabase()
    {
        using var db = _dbFactory.CreateContext();

        await TemplateStore.SyncAsync(db, default);

        var seeded = await db.MessageTemplateRows.CountAsync(r => r.Language == TemplateStore.RomanUrdu);
        Assert.Equal(MessageTemplates.EmbeddedEntries.Count, seeded);
    }

    [Fact]
    public async Task BrokenIndustryRow_IsIgnored_AndTheShippedKindOfShopStays()
    {
        using var db = _dbFactory.CreateContext();
        var title = IndustryProfiles.General.Title;
        db.MessageTemplateRows.Add(new MessageTemplateRow { Language = "industry", Key = "general", Text = "{ not json" });
        db.MessageTemplateRows.Add(new MessageTemplateRow { Language = "industry_english", Key = "general", Text = "{\"title\": \"\"}" });
        await db.SaveChangesAsync();

        await TemplateStore.SyncAsync(db, default);

        Assert.Equal("general", IndustryProfiles.General.Key);
        Assert.Equal(title, IndustryProfiles.General.Title);
    }

    [Fact]
    public async Task Sync_SeedsEveryShippedKindOfShopIntoTheDatabase()
    {
        using var db = _dbFactory.CreateContext();

        await TemplateStore.SyncAsync(db, default);

        var seeded = await db.MessageTemplateRows.CountAsync(r => r.Language == "industry");
        Assert.Equal(IndustryProfiles.EmbeddedTemplateJson.Count, seeded);
    }

    [Fact]
    public async Task ChangedShippedText_FollowsIntoUntouchedRows_AndNeverOverwritesAnEdit()
    {
        using var db = _dbFactory.CreateContext();
        // "untouched": text still matches the fingerprint of the older shipped text it was seeded with.
        db.MessageTemplateRows.Add(new MessageTemplateRow
        {
            Language = TemplateStore.RomanUrdu, Key = "count.unknown", Text = "OLD SHIPPED", SeedHash = SeedHash.Of("OLD SHIPPED"),
        });
        // "edited": an operator changed the text after it was seeded.
        db.MessageTemplateRows.Add(new MessageTemplateRow
        {
            Language = TemplateStore.RomanUrdu, Key = "setup.status.pending", Text = "OPERATOR EDIT", SeedHash = SeedHash.Of("SOME OLD SHIPPED"),
        });
        await db.SaveChangesAsync();

        await TemplateStore.SyncAsync(db, default);

        var untouched = await db.MessageTemplateRows.AsNoTracking().SingleAsync(r => r.Key == "count.unknown" && r.Language == TemplateStore.RomanUrdu);
        var edited = await db.MessageTemplateRows.AsNoTracking().SingleAsync(r => r.Key == "setup.status.pending" && r.Language == TemplateStore.RomanUrdu);
        Assert.Equal(MessageTemplates.EmbeddedEntries["count.unknown"], untouched.Text);
        Assert.Equal("OPERATOR EDIT", edited.Text);
    }

    [Fact]
    public void SeedHashDecision_FollowsOnlyRowsThatWereNeverEdited()
    {
        var shipped = SeedHash.Of("new");
        Assert.Equal(SeedAction.None, SeedHash.Decide(shipped, SeedHash.Of("anything"), shipped));
        Assert.Equal(SeedAction.Stamp, SeedHash.Decide(null, shipped, shipped));
        Assert.Equal(SeedAction.Leave, SeedHash.Decide(null, SeedHash.Of("stale or edited"), shipped));
        Assert.Equal(SeedAction.Update, SeedHash.Decide(SeedHash.Of("old"), SeedHash.Of("old"), shipped));
        Assert.Equal(SeedAction.Leave, SeedHash.Decide(SeedHash.Of("old"), SeedHash.Of("edited"), shipped));
    }

    [Fact]
    public async Task RuntimeRow_ForANewKey_IsUsedAfterSync_AndGoneWhenInactive()
    {
        using var db = _dbFactory.CreateContext();
        db.MessageTemplateRows.Add(new MessageTemplateRow { Language = TemplateStore.RomanUrdu, Key = RuntimeKey, Text = "Aap ka {0} tayyar hai" });
        await db.SaveChangesAsync();

        await TemplateStore.SyncAsync(db, default);
        Assert.Equal("Aap ka order tayyar hai", MessageTemplates.Get(RuntimeKey, "order"));

        var row = await db.MessageTemplateRows.SingleAsync(r => r.Key == RuntimeKey);
        row.Active = false;
        await db.SaveChangesAsync();

        await TemplateStore.SyncAsync(db, default);
        Assert.Throws<KeyNotFoundException>(() => MessageTemplates.Get(RuntimeKey, "order"));
    }
}
