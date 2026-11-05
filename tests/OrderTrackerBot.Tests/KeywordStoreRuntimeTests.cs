using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Generic miss words and feature areas held in the database: a row added at runtime is used after the next sync.</summary>
public class KeywordStoreRuntimeTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory = new();

    public KeywordStoreRuntimeTests() => KeywordStore.Reset();

    public void Dispose()
    {
        KeywordStore.Reset();
        _dbFactory.Dispose();
    }

    [Fact]
    public async Task GenericWordAddedInTheDatabase_IsUsedAfterSync()
    {
        using var db = _dbFactory.CreateContext();
        Assert.False(KeywordStore.IsGenericMissWord("zxqgeneric"));
        db.KeywordRules.Add(new KeywordRule { Word = "zxqgeneric", IntentId = KeywordStore.GenericGroup });
        await db.SaveChangesAsync();

        await KeywordStore.SyncAsync(db, default);

        Assert.True(KeywordStore.IsGenericMissWord("zxqgeneric"));
        Assert.True(KeywordStore.IsGenericMissWord("dikhao"));          // the shipped ones are still there
    }

    [Fact]
    public async Task FeatureAreaAddedInTheDatabase_IsOfferedAfterSync_AndABrokenPatternIsSkipped()
    {
        using var db = _dbFactory.CreateContext();
        db.FeatureAreas.Add(new FeatureAreaRow { Question = "Zxq area?", Pattern = "zxqarea", Options = "[\"zxq option\"]" });
        db.FeatureAreas.Add(new FeatureAreaRow { Question = "Broken area?", Pattern = "(unclosed", Options = "[\"x\"]" });
        await db.SaveChangesAsync();

        await KeywordStore.SyncAsync(db, default);

        Assert.True(ConversationEngine.TryKeywordClarification("please zxqarea now", out var question, out var options));
        Assert.Equal("Zxq area?", question);
        Assert.Equal(new[] { "zxq option" }, options);
        Assert.DoesNotContain(KeywordStore.CommandAreas, a => a.Question == "Broken area?");
    }
}
