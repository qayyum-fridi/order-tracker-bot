using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ResetConfirmationTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero));

    public ResetConfirmationTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object, clock: _clock);

    private async Task<ConversationEngine> OnboardWithProductAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        return engine;
    }

    private static async Task EditContextAsync(AppDbContext db, Action<SessionContextData> change)
    {
        var session = await db.Sessions.FirstAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        change(ctx);
        session.ContextJson = ctx.ToJson();
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task YesJustBeforeTheWindow_ResetsTheAccount()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardWithProductAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);

        _clock.Advance(ResetConfirmation.Window - TimeSpan.FromSeconds(1));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Empty(await db.Products.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task YesAtExactlyTheWindow_IsExpired_AndWipesNothing()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardWithProductAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);

        _clock.Advance(ResetConfirmation.Window);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.True(await db.Products.AnyAsync());
        Assert.Contains(_sent, m => m.Contains("purani confirmation"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task MissingGenerationId_FailsSafely_AndWipesNothing()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardWithProductAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await EditContextAsync(db, ctx => ctx.ResetGenerationId = null);

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.True(await db.Products.AnyAsync());
    }

    [Fact]
    public async Task MissingTimestamp_FailsSafely_AndWipesNothing()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardWithProductAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await EditContextAsync(db, ctx => ctx.ResetAskedAt = null);

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.True(await db.Products.AnyAsync());
    }

    [Fact]
    public async Task TimestampInTheFuture_IsRejected_AndWipesNothing()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardWithProductAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await EditContextAsync(db, ctx => ctx.ResetAskedAt = _clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.True(await db.Products.AnyAsync());
    }

    [Fact]
    public async Task StaleGeneration_CannotClaimANewerPrompt()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardWithProductAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        var session = await db.Sessions.FirstAsync();
        var current = SessionContextData.FromJson(session.ContextJson).ResetGenerationId!;

        Assert.False(await ResetConfirmation.TryClaimAsync(db, session.Id, "00000000000000000000000000000000", default));
        Assert.Equal(ConversationState.AwaitingResetConfirmation, (await db.Sessions.AsNoTracking().FirstAsync()).State);

        Assert.True(await ResetConfirmation.TryClaimAsync(db, session.Id, current, default));
        Assert.False(await ResetConfirmation.TryClaimAsync(db, session.Id, current, default));
    }

    [Fact]
    public async Task RepeatedYes_CannotExecuteTheResetAgain()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardWithProductAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Single(_sent, m => m.Contains("Account reset ho gaya"));
    }

    [Fact]
    public async Task No_StillCancelsWithoutTouchingData()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardWithProductAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "nahi", default);

        Assert.True(await db.Products.AnyAsync());
        Assert.Contains(_sent, m => m.Contains("kuch delete nahi kiya"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    public void Dispose() => _dbFactory.Dispose();

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
