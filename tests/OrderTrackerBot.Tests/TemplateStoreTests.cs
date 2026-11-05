using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Conversation;
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
