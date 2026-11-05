using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>A seller's own question about their records is never turned into a reply draft for a customer.</summary>
public class SellerQuestionTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();
    private readonly List<string> _sent = new();

    public SellerQuestionTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _dbFactory.Dispose();

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, null, _media.Object);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "Lawn Suit - 3500", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        _sent.Clear();
        return engine;
    }

    [Fact]
    public async Task SellersOwnOrderCountQuestion_IsNotTurnedIntoACustomerReplyDraft()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        // The model read the seller's question as a support query, but no buyer is named.
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "support_query",
                SupportQuery = new AiSupportQuery { CustomerName = null, Question = "Mere paas orders kitne hain" },
            });

        await engine.HandleIncomingMessageAsync(Phone, "Mere paas orders kitne hain", default);

        Assert.DoesNotContain(_sent, m => m.Contains("Customer query samjha"));
        Assert.Equal(0, await db.SupportQueries.CountAsync());
    }

    [Theory]
    [InlineData("3")]                    // the item's number
    [InlineData("Customer ki detail")]   // the exact label
    public async Task AnswerToThePictureTypeList_LabelsThePicture_NotAnOrderLookup(string answer)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        _media.Setup(m => m.DownloadAsync("pic-type", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[3000], "image/jpeg"));
        _ai.Setup(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear" });
        _ai.Setup(a => a.ReadPictureAsync("customer", It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiPictureReading { Name = "Sara", Phone = "03001234567" });
        await engine.HandleImageMessageAsync(Phone, "pic-type", null);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, answer, default);

        _ai.Verify(a => a.ReadPictureAsync("customer", It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain(_sent, m => m.Contains("Order #3 nahi mila"));
    }

    [Fact]
    public async Task ShortSpokenCount_IsRoutedAsSaid_NotThroughTheWrongRewrite()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), "audio/ogg", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("mere paas kitne customers hain");
        _media.Setup(m => m.DownloadAsync("voice-count", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[2500], "audio/ogg"));
        // The rewrite got the words wrong; the router, given the words as said, places the count intent.
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation
            {
                Steps = { "Kis tarah ka customer hai?" }, Actions = { "command" }, Spans = { "mere paas kitne customers hain" },
            });
        var count = new OrderTrackerBot.Application.Intents.IntentRoute("count.total", new Dictionary<string, string> { ["entity"] = "customers" }, 0.9);
        _ai.Setup(a => a.RouteIntentAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<OrderTrackerBot.Application.Intents.IntentDefinition>>(), It.IsAny<CancellationToken>())).ReturnsAsync(count);
        _ai.Setup(a => a.RouteIntentWithContextAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<OrderTrackerBot.Application.Intents.IntentDefinition>>(), It.IsAny<AiRouteContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(count);
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, null, _media.Object, transcriber: transcriber.Object);
        _sent.Clear();

        await engine.HandleAudioMessageAsync(Phone, "voice-count", "wamid.count");

        Assert.Contains(_sent, m => m.Contains("customers hain"));
    }
}
