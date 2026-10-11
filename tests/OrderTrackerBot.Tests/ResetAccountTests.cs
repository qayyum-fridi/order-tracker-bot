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

public class ResetAccountTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public ResetAccountTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    [Fact]
    public async Task ResetAccount_RemovesAllProducts()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        await engine.HandleIncomingMessageAsync(Phone, "Lawn Suit - 3500", default);
        Assert.True(await db.Products.AnyAsync());

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Empty(await db.Products.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ResetAccount_StaleYes_WipesNothing()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        var session = await db.Sessions.FirstAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.ResetAskedAt = DateTime.UtcNow.AddHours(-1);
        session.ContextJson = ctx.ToJson();
        await db.SaveChangesAsync();
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.True(await db.Products.AnyAsync());
        Assert.Contains(_sent, m => m.Contains("purani confirmation"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task ResetAccount_RemovesProductsThatHavePriceTiers()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        var kurti = await db.Products.FirstAsync();
        db.PriceTiers.Add(new PriceTier { ProductId = kurti.Id, MinQty = 10, PricePerUnit = 1500 });
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Empty(await db.Products.AsNoTracking().ToListAsync());
    }

    public void Dispose() => _dbFactory.Dispose();
}
