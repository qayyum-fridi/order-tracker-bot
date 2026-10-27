using System.Text.RegularExpressions;
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

/// <summary>"login" typed on WhatsApp goes through the engine and sends a code to the same chat.</summary>
public class ClientLoginFlowTests : IDisposable
{
    private const string Phone = "923124891614";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _text = new();

    public ClientLoginFlowTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    public void Dispose() { }

    private async Task<AppDbContext> SellerAsync(bool onboarded, ConversationState state = ConversationState.Idle)
    {
        var db = _dbFactory.CreateContext();
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = onboarded, Session = new ConversationSession { State = state } });
        await db.SaveChangesAsync();
        return db;
    }

    private async Task<AppDbContext> SendAsync(AppDbContext db, string text)
    {
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false });
        await engine.HandleIncomingMessageAsync(Phone, text, default);
        return db;
    }

    [Fact]
    public async Task Login_on_a_finished_account_sends_a_six_digit_code_to_the_chat()
    {
        using var db = await SellerAsync(onboarded: true);
        await SendAsync(db, "login");

        var reply = Assert.Single(_text);
        var code = Regex.Match(reply, @"\b(\d{6})\b").Groups[1].Value;
        Assert.Equal(6, code.Length);
        var seller = await db.Sellers.SingleAsync();
        Assert.NotNull(seller.ClientLoginCodeHash);
    }

    [Fact]
    public async Task Login_while_onboarding_is_not_swallowed()
    {
        using var db = await SellerAsync(onboarded: false);
        await SendAsync(db, "login");

        Assert.Contains(_text, t => Regex.IsMatch(t, @"\b\d{6}\b"));
    }

    [Fact]
    public async Task Login_while_an_order_is_waiting_for_confirmation_is_not_swallowed()
    {
        using var db = await SellerAsync(onboarded: true, state: ConversationState.AwaitingOrderConfirmation);
        await SendAsync(db, "login");

        Assert.Contains(_text, t => Regex.IsMatch(t, @"\b\d{6}\b"));
    }

    [Fact]
    public async Task Dashboard_logout_during_onboarding_ends_the_session()
    {
        using var db = await SellerAsync(onboarded: false);
        var seller = await db.Sellers.SingleAsync();
        seller.ClientApiTokenHash = "old-token-hash";
        await db.SaveChangesAsync();

        await SendAsync(db, "dashboard logout");

        Assert.Null((await db.Sellers.SingleAsync()).ClientApiTokenHash);
        Assert.Single(_text);
    }
}
