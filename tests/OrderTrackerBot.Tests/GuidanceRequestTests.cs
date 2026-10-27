using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// "What do I do now / what is left of the setup?", typed or spoken, is a request for the next step: it gets the tip for the current step.
public class GuidanceRequestTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string Spoken = "سیک اپ میں ابھی کیا چیز باقی ہے؟"; // the live voice note: the speech model wrote "سیک اپ" for "setup"
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public GuidanceRequestTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
    }

    [Theory]
    [InlineData(Spoken)]
    [InlineData("سیٹ اپ میں ابھی کیا چیز باقی ہے؟")]
    [InlineData("setup mein abhi kya cheez baqi hai?")]
    [InlineData("set up me kya baki hai")]
    [InlineData("ab kya karna hai")]
    [InlineData("agla kaam kya hai?")]
    [InlineData("اب کیا کرنا ہے؟")]
    [InlineData("کیا کروں؟")]
    public void AskingForTheNextStep_IsAGuidanceRequest(string text) => Assert.True(CommandParser.IsGuidanceRequest(text));

    [Theory]
    [InlineData("kya baqi hai")]
    [InlineData("Sara ka balance kya baqi hai")]
    [InlineData("orders today")]
    [InlineData("setup")]
    public void OtherPhrases_AreNot(string text) => Assert.False(CommandParser.IsGuidanceRequest(text));

    private async Task<ConversationEngine> AtTheProductStepAsync(AppDbContext db, Mock<IAudioTranscriber>? transcriber = null)
    {
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync("voice-1", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "audio/ogg"));
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: media.Object, transcriber: transcriber?.Object);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Kurti - 1800" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
        return engine;
    }

    [Fact]
    public async Task TypedAtTheProductStep_AnswersWithATip_NotWithTheOrderRefusal()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await AtTheProductStepAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, Spoken, default);

        Assert.NotEmpty(_sent);
        Assert.DoesNotContain(_sent, m => m.Contains("order darj nahi ho sakta"));
    }

    [Fact]
    public async Task SpokenAtTheProductStep_AnswersWithATip_WithoutTheRewriteOrTheNotClearWarning()
    {
        using var db = _dbFactory.CreateContext();
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), "audio/ogg", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Spoken);
        var engine = await AtTheProductStepAsync(db, transcriber);

        await engine.HandleAudioMessageAsync(Phone, "voice-1");

        _ai.Verify(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(_sent, m => m.Contains("Maine suna"));
        Assert.DoesNotContain(_sent, m => m.Contains("saaf nahi"));
        Assert.DoesNotContain(_sent, m => m.Contains("order darj nahi ho sakta"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
