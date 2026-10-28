using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Nobody should have to type "done": once a product is saved, a real command ends setup and runs.
public class SetupCompletionTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public SetupCompletionTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
    }

    private async Task<ConversationEngine> AtTheProductStepAsync(AppDbContext db, params string[] products)
    {
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip" }.Concat(products))
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
        return engine;
    }

    [Fact]
    public async Task ARealCommand_AfterAProduct_EndsSetup_AndRuns()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await AtTheProductStepAsync(db, "Kurti - 1800");

        await engine.HandleIncomingMessageAsync(Phone, "expense 500 packaging", default);

        var seller = await db.Sellers.SingleAsync();
        Assert.True(seller.OnboardingComplete);
        Assert.Equal(ConversationState.Idle, (await db.Sessions.SingleAsync()).State);
        Assert.Single(await db.Expenses.ToListAsync());
        Assert.Contains(_sent, m => m.Contains("setup mukammal"));
        Assert.DoesNotContain(_sent, m => m.Contains("order darj nahi ho sakta"));
    }

    [Fact]
    public async Task ACustomerAfterAProduct_EndsSetup_WithoutDone()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await AtTheProductStepAsync(db, "Kurti - 1800");

        await engine.HandleIncomingMessageAsync(Phone, "customer add Sara, Model Town, 03001234567", default);

        Assert.True((await db.Sellers.SingleAsync()).OnboardingComplete);
        Assert.Equal(ConversationState.Idle, (await db.Sessions.SingleAsync()).State);
        Assert.True(await db.Customers.AnyAsync(c => c.Name == "Sara"));
        Assert.Contains(_sent, m => m.Contains("customer list"));
        Assert.Contains(_sent, m => m.Contains("setup mukammal"));
    }

    [Fact]
    public async Task ACustomerBeforeAnyProduct_KeepsSetupOpen()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await AtTheProductStepAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "customer add Sara, Model Town, 03001234567", default);

        Assert.False((await db.Sellers.SingleAsync()).OnboardingComplete);
        Assert.True(await db.Customers.AnyAsync(c => c.Name == "Sara"));
    }

    [Fact]
    public async Task ARealCommand_WithNoProductYet_StillWaits_AndSkipIsTheWayOut()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await AtTheProductStepAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "expense 500 packaging", default);

        Assert.False((await db.Sellers.SingleAsync()).OnboardingComplete);
        Assert.Empty(await db.Expenses.ToListAsync());
        Assert.Contains(_sent, m => m.Contains("order darj nahi ho sakta"));
    }

    [Fact]
    public async Task ReadOnlyLookups_AfterAProduct_DoNotEndSetup()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await AtTheProductStepAsync(db, "Kurti - 1800");

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.False((await db.Sellers.SingleAsync()).OnboardingComplete);
    }

    public void Dispose() => _dbFactory.Dispose();
}
