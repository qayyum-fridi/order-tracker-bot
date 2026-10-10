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

public class SellerStatusTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public SellerStatusTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
    }

    [Theory]
    [InlineData(SellerStatus.Disabled, "abhi band hai")]
    [InlineData(SellerStatus.Cancelled, "band ho chuka hai")]
    public async Task NonActiveSeller_GetsOnlyTheBlockReply(SellerStatus status, string expectedText)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        (await db.Sellers.FirstAsync()).Status = status;
        await db.SaveChangesAsync();

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "orders today", default);

        var reply = Assert.Single(_sent);
        Assert.Contains(expectedText, reply);
    }

    [Fact]
    public async Task ActiveSeller_IsProcessedNormally_AfterBeingReEnabled()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var seller = await db.Sellers.FirstAsync();
        seller.Status = SellerStatus.Disabled;
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);
        _sent.Clear();

        seller.Status = SellerStatus.Active;
        await db.SaveChangesAsync();
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.DoesNotContain(_sent, m => m.Contains("account"));
        Assert.NotEmpty(_sent);
    }

    public void Dispose() => _dbFactory.Dispose();
}
