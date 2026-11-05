using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Intent keywords: whole-word matching, and the shared list of words the AI router learned (votes, hints).</summary>
public class LearnedKeywordTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory = new();

    public LearnedKeywordTests()
    {
        LearnedKeywordLog.Clear();
        KeywordStore.Reset();
    }

    public void Dispose()
    {
        LearnedKeywordLog.Clear();
        KeywordStore.Reset();
        _dbFactory.Dispose();
    }

    [Fact]
    public async Task StopWordAddedInTheDatabase_IsIgnoredAfterSync_AndIsUsedAgainWhenDisabled()
    {
        using var db = _dbFactory.CreateContext();
        db.KeywordRules.Add(new OrderTrackerBot.Domain.Entities.KeywordRule { Word = "zxqlearn" });
        await db.SaveChangesAsync();

        await KeywordStore.SyncAsync(db, default);
        Assert.DoesNotContain("zxqlearn", IntentKeywords.Tokens("zxqlearn bikri"));
        Assert.Contains("bikri", IntentKeywords.Tokens("zxqlearn bikri"));

        var rule = await db.KeywordRules.SingleAsync(r => r.Word == "zxqlearn");
        rule.Active = false;
        await db.SaveChangesAsync();

        await KeywordStore.SyncAsync(db, default);
        Assert.Contains("zxqlearn", IntentKeywords.Tokens("zxqlearn bikri"));
    }

    private static IntentDefinition Intent(string id, params string[] keywords) =>
        new(id, "order", "show", "OrderDetail", "test", new[] { "example" }, Array.Empty<IntentSlot>(), "orders today", keywords);

    [Fact]
    public void Keyword_MatchesWholeWordsOnly()
    {
        var intents = new[] { Intent("show", "order"), Intent("catalog", "catalog") };

        Assert.Equal(new[] { "show" }, IntentKeywords.Match("order dikhao", intents));
        Assert.Empty(IntentKeywords.Match("orders", intents));          // "order" is not "orders"
        Assert.Empty(IntentKeywords.Match("", intents));
    }

    [Fact]
    public void MultiWordKeyword_MatchesOnlyWhenAllItsWordsAppearInARow()
    {
        var intents = new[] { Intent("low", "stock low") };

        Assert.Equal(new[] { "low" }, IntentKeywords.Match("stock low dikhao", intents));
        Assert.Empty(IntentKeywords.Match("stock dikhao low", intents));
    }

    [Fact]
    public void Tokens_DropStopWordsShortWordsAndNumbers()
    {
        var words = IntentKeywords.Tokens("Sara ka order 3 ki bikri aaj");

        Assert.Contains("sara", words);
        Assert.Contains("order", words);
        Assert.Contains("bikri", words);
        Assert.DoesNotContain("ka", words);
        Assert.DoesNotContain("aaj", words);
        Assert.DoesNotContain("3", words);
    }

    [Fact]
    public async Task LearnedWord_BecomesAHint_OnlyAfterMinVotes()
    {
        using var db = _dbFactory.CreateContext();
        var now = DateTime.UtcNow;

        for (var i = 0; i < LearnedKeywordLog.MinVotes - 1; i++)
        {
            LearnedKeywordLog.Record(new[] { "bikri" }, "order.list_today");
            await LearnedKeywordLog.FlushAsync(db, now, default);
        }
        Assert.Empty(LearnedKeywordLog.HintsFor(new[] { "bikri" }));

        LearnedKeywordLog.Record(new[] { "bikri" }, "order.list_today");
        await LearnedKeywordLog.FlushAsync(db, now, default);

        Assert.Equal(new[] { "order.list_today" }, LearnedKeywordLog.HintsFor(new[] { "bikri" }));
        var stored = await db.LearnedKeywords.AsNoTracking().SingleAsync(k => k.Word == "bikri");
        Assert.Equal(LearnedKeywordLog.MinVotes, stored.Votes);
        Assert.Equal("order.list_today", stored.IntentId);
    }

    [Fact]
    public async Task WordLearnedForTwoIntents_ReturnsTheStrongerOne()
    {
        using var db = _dbFactory.CreateContext();
        var now = DateTime.UtcNow;
        LearnedKeywordLog.Record(new[] { "dhoondo" }, "customer.search");
        LearnedKeywordLog.Record(new[] { "dhoondo" }, "catalog.search");
        LearnedKeywordLog.Record(new[] { "dhoondo" }, "catalog.search");
        LearnedKeywordLog.Record(new[] { "dhoondo" }, "catalog.search");
        await LearnedKeywordLog.FlushAsync(db, now, default);

        Assert.Equal(new[] { "catalog.search" }, LearnedKeywordLog.HintsFor(new[] { "dhoondo" }));
    }
}
