using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Features:InsightRouter: a question the rules read as an insight is offered to the intent router first. Off by default, so the rules answer as before.
public class InsightRouterTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string Question = "what did Sara buy last time";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IFounderAlertNotifier> _founder = new();

    public InsightRouterTests()
    {
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private async Task SeedAsync(AppDbContext db)
    {
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } });
        await db.SaveChangesAsync();
    }

    private ConversationEngine Engine(AppDbContext db, bool insightRouter) =>
        new(db, _ai.Object, _sender.Object, _founder.Object, features: new FeatureOptions { ShortcutButtons = false, InsightRouter = insightRouter });

    [Fact]
    public async Task FlagOn_InsightQuestion_IsOfferedToTheRouter()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync(Question, It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("customer.list", new Dictionary<string, string>(), 0.95));

        await Engine(db, insightRouter: true).HandleIncomingMessageAsync(Phone, Question, default);

        _ai.Verify(a => a.RouteIntentAsync(Question, It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task FlagOff_InsightQuestion_KeepsTheRules_AndDoesNotAskTheRouter()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db, insightRouter: false).HandleIncomingMessageAsync(Phone, Question, default);

        _ai.Verify(a => a.RouteIntentAsync(Question, It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void FeatureDefault_IsOff()
    {
        Assert.False(new FeatureOptions().InsightRouter);
    }

    public void Dispose() => _dbFactory.Dispose();
}
