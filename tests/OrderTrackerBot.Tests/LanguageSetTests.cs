using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// "Urdu mein karo" / "اردو ma تبدیل کردیا" switch the language at once; a bare "change language" still shows the picker.
public class LanguageSetTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IFounderAlertNotifier> _founder = new();

    public LanguageSetTests()
    {
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    [Theory]
    [InlineData("urdu mein karo", "اردو")]
    [InlineData("اردو ma تبدیل کردیا", "اردو")]
    [InlineData("language urdu", "اردو")]
    [InlineData("change language to english", "english")]
    [InlineData("english mein kar do", "english")]
    [InlineData("roman urdu mein karo", "roman urdu")]
    public void NamedLanguage_ParsesToTheLanguageSwitch(string message, string target)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.ChangeLanguage, parsed!.Kind);
        Assert.Equal(target, parsed.Text);
    }

    [Theory]
    [InlineData("change language")]
    [InlineData("language")]
    public void NoLanguageNamed_KeepsThePicker(string message)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.ChangeLanguage, parsed?.Kind);
        Assert.Null(parsed!.Text);
    }

    [Fact]
    public async Task UrduScriptRequest_SwitchesTheSellerToUrdu_WithoutThePicker()
    {
        using var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, PreferredLanguage = Lang.RomanUrdu, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founder.Object, features: new FeatureOptions { ShortcutButtons = false });

        await engine.HandleIncomingMessageAsync(Phone, "اردو ma تبدیل کردیا", default);

        Assert.Equal(Lang.UrduScript, (await db.Sellers.AsNoTracking().FirstAsync()).PreferredLanguage);
        _sender.Verify(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose() => _dbFactory.Dispose();
}
