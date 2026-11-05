using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>When the router is not sure, its guess is offered as a Yes / No question; Yes runs that command.</summary>
public class AiGuessTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();
    private readonly List<string> _sent = new();
    private readonly List<(string Body, IReadOnlyList<string> Buttons)> _buttons = new();

    public AiGuessTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, buttons, _) => { _buttons.Add((body, buttons)); _sent.Add(body); })
            .Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear" });
    }

    public void Dispose() => _dbFactory.Dispose();

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, null, _media.Object);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "Lawn Suit - 3500", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        _sent.Clear();
        _buttons.Clear();
        return engine;
    }

    [Fact]
    public async Task TypedSetupComplete_ReportsSetupStatus()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "setup complete", default);

        Assert.Contains(_sent, m => m.Contains("setup mukammal"));
    }

    [Fact]
    public async Task UnsureGuess_IsOfferedAsYesNo_AndYesRunsTheGuessedCommand()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var guess = new IntentRoute("setup.status", new Dictionary<string, string>(), 0.5);
        _ai.Setup(a => a.RouteIntentAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>())).ReturnsAsync(guess);
        _ai.Setup(a => a.RouteIntentWithContextAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<AiRouteContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(guess);

        await engine.HandleIncomingMessageAsync(Phone, "how many my inventory is", default);

        var asked = Assert.Single(_buttons, b => b.Body.Contains("setup complete"));
        Assert.Equal(new[] { "✅ Haan, sahi hai", "❌ Nahi" }, asked.Buttons);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "✅ Haan, sahi hai", default);
        Assert.Contains(_sent, m => m.Contains("setup mukammal"));

        // The router's confidence and the seller's answer are kept, so the 0.4 / 0.7 bars can be tuned.
        var decisions = await db.RouterDecisions.AsNoTracking().ToListAsync();
        Assert.Contains(decisions, d => d.Outcome == "guess" && d.IntentId == "setup.status" && d.Confidence == 0.5);
        Assert.Contains(decisions, d => d.Outcome == "guess_yes" && d.IntentId == "setup.status" && d.Confidence == 0.5);
    }

    [Fact]
    public async Task TapOnTheKeywordQuestion_TeachesTheMessageWords()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        // The router has no answer: the keyword question about stock is what the seller sees.
        await engine.HandleIncomingMessageAsync(Phone, "how many my inventory is", default);
        Assert.Contains(_sent, m => m.Contains("Stock ke baare mein"));

        await engine.HandleIncomingMessageAsync(Phone, "stock", default);

        Assert.True(await db.LearnedKeywordVotes.AnyAsync(v => v.Word == "inventory"), "no vote for the message's words");

        // The same seller asking again is answered directly, not with the keyword question.
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "how many my inventory is", default);
        Assert.DoesNotContain(_sent, m => m.Contains("Stock ke baare mein"));
    }

    [Fact]
    public async Task WordThreeSellersAgreedOn_RoutesTheSameWayForAnyone()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        foreach (var other in new[] { 101, 102, 103 })
            await LearnedKeywordLog.AddVotesAsync(db, other, new[] { "zxqshared" }, "setup.status", default);
        await db.SaveChangesAsync();
        await LearnedKeywordLog.FlushAsync(db, DateTime.UtcNow, default);

        await engine.HandleIncomingMessageAsync(Phone, "zxqshared", default);

        Assert.Contains(_sent, m => m.Contains("setup mukammal"));
    }
}
