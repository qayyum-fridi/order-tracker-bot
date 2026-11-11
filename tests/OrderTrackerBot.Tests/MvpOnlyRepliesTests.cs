using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Features:MvpOnly: every feature cut from the MVP answers one "not available yet" reply and runs nothing.
public class MvpOnlyRepliesTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _text = new();
    private readonly List<string> _buttons = new();

    public MvpOnlyRepliesTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _buttons.Add(b)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    public void Dispose() => _dbFactory.Dispose();

    // One message per cut feature: discounts, loyalty, payments link, payouts, drawer, labels, Instagram, import, walk-in, forms, scan.
    public static IEnumerable<object[]> CutFeatureMessages() => new[]
    {
        new object[] { "loyal customers" },
        new object[] { "payout pending" },
        new object[] { "drawer open 5000" },
        new object[] { "label 1" },
        new object[] { "connect instagram" },
        new object[] { "import" },
        new object[] { "walkin Shirt 1" },
        new object[] { "scan cart" },
    };

    [Theory]
    [MemberData(nameof(CutFeatureMessages))]
    public async Task CutFeature_AnswersOneNotAvailableReply_AndRunsNothing(string message)
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var products = await db.Products.CountAsync();
        var orders = await db.Orders.CountAsync();
        var actions = await db.ActionLogs.CountAsync();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { MvpOnly = true, ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false });

        await engine.HandleIncomingMessageAsync(Phone, message, default);

        var reply = Assert.Single(_text);
        Assert.Equal(MessageTemplates.Get("mvp.001"), reply);
        Assert.Empty(_buttons);
        Assert.Equal(products, await db.Products.CountAsync());
        Assert.Equal(orders, await db.Orders.CountAsync());
        Assert.Equal(actions, await db.ActionLogs.CountAsync());
        _text.Clear();
        _buttons.Clear();
        db.Dispose();
    }
}
