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

// A YES in a confirmation state acts only on the thing that state asked about. With nothing bound to the question, it must change nothing.
public class ConfirmationScopeTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();

    public ConfirmationScopeTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    [Theory]
    [InlineData(ConversationState.AwaitingCancelConfirmation)]
    [InlineData(ConversationState.AwaitingCodCollectedConfirmation)]
    [InlineData(ConversationState.AwaitingDeleteCustomerConfirmation)]
    [InlineData(ConversationState.AwaitingLoyaltyDiscountConfirmation)]
    [InlineData(ConversationState.AwaitingBulkStatusConfirmation)]
    public async Task Yes_WithNothingBound_ChangesNothing_AndReturnsToIdle(ConversationState state)
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        var session = await db.Sessions.FirstAsync();
        session.State = state;
        session.ContextJson = new SessionContextData().ToJson();
        await db.SaveChangesAsync();
        var productsBefore = await db.Products.CountAsync();

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(productsBefore, await db.Products.CountAsync());
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    public void Dispose() => _dbFactory.Dispose();
}
