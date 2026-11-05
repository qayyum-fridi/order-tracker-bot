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

// "reports" shows the report list as one tap menu; every row is a command the parser already reads.
public class ReportMenuTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IFounderAlertNotifier> _founder = new();

    public ReportMenuTests()
    {
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    [Theory]
    [InlineData("reports")]
    [InlineData("Reports")]
    public void ReportsWord_IsTheReportMenu(string text) =>
        Assert.Equal(CommandKind.ReportMenu, CommandParser.TryParse(text)?.Kind);

    [Fact]
    public void EveryRow_IsARealCommand()
    {
        foreach (var row in ConversationEngine.ReportMenuRows)
        {
            var parsed = CommandParser.TryParse(row.Id);
            Assert.True(parsed is not null && parsed.Kind != CommandKind.Notice, $"row '{row.Id}' is not a command");
        }
    }

    [Fact]
    public async Task ReportsWord_SendsTheListWithAllRows()
    {
        using var db = _dbFactory.CreateContext();
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } });
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founder.Object, features: new FeatureOptions { ShortcutButtons = false });

        await engine.HandleIncomingMessageAsync(Phone, "reports", default);

        _sender.Verify(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(),
            It.Is<IReadOnlyList<MenuSection>>(sections => sections.Single().Rows.Count == ConversationEngine.ReportMenuRows.Length),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose() => _dbFactory.Dispose();
}
