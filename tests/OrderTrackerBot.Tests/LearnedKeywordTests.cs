using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Word votes from behaviour: one vote per seller and word, promotion after distinct sellers, retraction on correction.</summary>
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

    private static IntentDefinition Intent(string id, params string[] keywords) =>
        new(id, "order", "show", "OrderDetail", "test", new[] { "example" }, Array.Empty<IntentSlot>(), "orders today", keywords);

    /// <summary>One seller's vote for the words, saved the way a turn saves it.</summary>
    private static async Task VoteAsync(AppDbContext db, int seller, string intent, params string[] words)
    {
        await LearnedKeywordLog.AddVotesAsync(db, seller, words, intent, default);
        await db.SaveChangesAsync();
    }

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
    public async Task WordBecomesAHint_OnlyAfterMinVotes_FromDistinctSellers()
    {
        using var db = _dbFactory.CreateContext();
        var now = DateTime.UtcNow;

        for (var seller = 1; seller < LearnedKeywordLog.MinVotes; seller++)
        {
            await VoteAsync(db, seller, "order.list_today", "bikri");
            await LearnedKeywordLog.FlushAsync(db, now, default);
        }
        Assert.Empty(LearnedKeywordLog.HintsFor(new[] { "bikri" }));

        await VoteAsync(db, LearnedKeywordLog.MinVotes, "order.list_today", "bikri");
        await LearnedKeywordLog.FlushAsync(db, now, default);

        Assert.Equal(new[] { "order.list_today" }, LearnedKeywordLog.HintsFor(new[] { "bikri" }));
        var stored = await db.LearnedKeywords.AsNoTracking().SingleAsync(k => k.Word == "bikri");
        Assert.Equal(LearnedKeywordLog.MinVotes, stored.Votes);
    }

    [Fact]
    public async Task OneSellerRepeatingAWord_NeverMakesItAHint()
    {
        using var db = _dbFactory.CreateContext();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            await VoteAsync(db, 7, "order.list_today", "zxqspam");
            await LearnedKeywordLog.FlushAsync(db, now, default);
        }

        Assert.Empty(LearnedKeywordLog.HintsFor(new[] { "zxqspam" }));
        var stored = await db.LearnedKeywords.AsNoTracking().SingleAsync(k => k.Word == "zxqspam");
        Assert.Equal(1, stored.Votes);
    }

    [Fact]
    public async Task WordLearnedForTwoIntents_ReturnsTheStrongerOne()
    {
        using var db = _dbFactory.CreateContext();
        var now = DateTime.UtcNow;
        await VoteAsync(db, 10, "customer.search", "dhoondo");
        await VoteAsync(db, 1, "catalog.search", "dhoondo");
        await VoteAsync(db, 2, "catalog.search", "dhoondo");
        await VoteAsync(db, 3, "catalog.search", "dhoondo");
        await LearnedKeywordLog.FlushAsync(db, now, default);

        Assert.Equal(new[] { "catalog.search" }, LearnedKeywordLog.HintsFor(new[] { "dhoondo" }));
    }

    [Fact]
    public async Task Retract_RemovesAnOrdersVotes_AndTakesOnePointOffACountedWord()
    {
        using var db = _dbFactory.CreateContext();
        var now = DateTime.UtcNow;
        for (var seller = 1; seller <= LearnedKeywordLog.MinVotes; seller++)
            await VoteAsync(db, seller, "order.list_today", "bikri");
        await LearnedKeywordLog.FlushAsync(db, now, default);
        Assert.Equal(new[] { "order.list_today" }, LearnedKeywordLog.HintsFor(new[] { "bikri" }));

        // seller 2's order was cancelled within the hour: that one vote goes
        var votes = await db.LearnedKeywordVotes.Where(v => v.SellerId == 2).ToListAsync();
        LearnedKeywordLog.LinkToOrder(votes, 500);
        await db.SaveChangesAsync();
        await LearnedKeywordLog.RetractAsync(db, 2, 500, default);
        await db.SaveChangesAsync();
        await LearnedKeywordLog.FlushAsync(db, now, default);

        var stored = await db.LearnedKeywords.AsNoTracking().SingleAsync(k => k.Word == "bikri");
        Assert.Equal(LearnedKeywordLog.MinVotes - 1, stored.Votes);
        Assert.Empty(LearnedKeywordLog.HintsFor(new[] { "bikri" }));  // below the threshold: the hint is stripped
    }
}
