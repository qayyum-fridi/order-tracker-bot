using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>A cash entry or a screenshot sent while the phone was offline is stamped with when it was sent, not when the bot read it.</summary>
public class OfflineSendTimeTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public OfflineSendTimeTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: _media.Object);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
        return engine;
    }

    [Fact]
    public async Task ACashEntrySentWhileOffline_KeepsItsSendTime()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "drawer open 5000", default);
        var open = await db.CashDrawerSessions.SingleAsync();
        open.OpenedAt = DateTime.UtcNow.AddHours(-3);
        await db.SaveChangesAsync();

        var sentAt = DateTime.UtcNow.AddHours(-2);
        await engine.HandleIncomingMessageAsync(Phone, "cash in 500 udhaar mila", default, sentAt);

        var movement = await db.CashMovements.SingleAsync();
        Assert.True(Math.Abs((movement.CreatedAt - sentAt).TotalSeconds) < 5, $"stamped {movement.CreatedAt:O}, sent {sentAt:O}");
    }

    [Fact]
    public async Task ACashEntrySentBeforeTheDrawerOpened_CountsFromWhenItOpened()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "drawer open 5000", default);
        var open = await db.CashDrawerSessions.SingleAsync();
        open.OpenedAt = DateTime.UtcNow.AddHours(-3);
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "cash in 500", default, DateTime.UtcNow.AddHours(-5));

        var movement = await db.CashMovements.SingleAsync();
        Assert.Equal(open.OpenedAt, movement.CreatedAt);
    }

    [Fact]
    public async Task AScreenshotSentWhileOffline_IsSavedWithTheTimeItWasSent()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        _media.Setup(m => m.DownloadAsync("media-9", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "image/jpeg"));
        _ai.Setup(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order", IsOrderAttempt = true,
                Order = new AiOrderDraft { CustomerName = "Ramsha", Phone = "03001112222", Items = { new AiOrderItemDraft { ProductName = "Kurti", Quantity = 1 } } }
            });

        var sentAt = DateTime.UtcNow.AddHours(-2);
        await engine.HandleImageMessageAsync(Phone, "media-9", null, default, sentAt);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var order = await db.Orders.SingleAsync();
        Assert.True(Math.Abs((order.CreatedAt - sentAt).TotalSeconds) < 5, $"stamped {order.CreatedAt:O}, sent {sentAt:O}");
    }

    public void Dispose() => _dbFactory.Dispose();
}
