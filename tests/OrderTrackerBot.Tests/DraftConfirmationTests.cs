using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// A YES/"ok" saves an order draft only while the draft is recent (10 minutes from when it was first shown).
public class DraftConfirmationTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public DraftConfirmationTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Sara",
                    Phone = "03009876543",
                    Address = "Gulberg Lahore",
                    Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } }
                }
            });
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task<(ConversationEngine Engine, AppDbContext Db)> DraftShownAsync()
    {
        var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03009876543, Gulberg Lahore", default);
        Assert.Contains(_sent, m => m.Contains("Confirm order"));
        return (engine, db);
    }

    [Fact]
    public async Task LateYes_OnAnOldDraft_IsNotSaved()
    {
        var (engine, db) = await DraftShownAsync();
        var session = await db.Sessions.FirstAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.PendingOrderAskedAt = DateTime.UtcNow.AddMinutes(-11);
        session.ContextJson = ctx.ToJson();
        await db.SaveChangesAsync();
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Contains(_sent, m => m.Contains("purana ho gaya"));
    }

    [Fact]
    public async Task LateOk_OnAnOldDraft_IsNotSaved()
    {
        var (engine, db) = await DraftShownAsync();
        var session = await db.Sessions.FirstAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.PendingOrderAskedAt = DateTime.UtcNow.AddMinutes(-30);
        session.ContextJson = ctx.ToJson();
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "ok", default);

        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Yes_OnAFreshDraft_StillSaves()
    {
        var (engine, db) = await DraftShownAsync();

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Yes_WithNoTimestamp_IsNotSaved()
    {
        var (engine, db) = await DraftShownAsync();
        var session = await db.Sessions.FirstAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.PendingOrderAskedAt = null;
        session.ContextJson = ctx.ToJson();
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(0, await db.Orders.CountAsync());
    }

    public void Dispose() => _dbFactory.Dispose();
}
