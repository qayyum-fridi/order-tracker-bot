using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class OnboardingCustomerTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string Message = "naya client hai Muhammad Ali, address Model Town Lahore, phone baad mein";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public OnboardingCustomerTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task OnboardAsync(AppDbContext db) =>
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Kurti - 1800");

    /// <summary>Plays the first setup steps and stops, so the next message lands on that step.</summary>
    private async Task OnboardUntilAsync(AppDbContext db, params string[] steps)
    {
        var engine = CreateEngine(db);
        foreach (var step in steps)
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
    }

    private void AnalysisIs(AiMessageAnalysis analysis) =>
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(analysis);

    [Fact]
    public async Task ClientNamedDuringSetup_WithoutItems_IsSavedAsCustomer()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        AnalysisIs(new AiMessageAnalysis
        {
            Intent = "add_customer",
            NewCustomer = new AiNewCustomer { Name = "Muhammad Ali", Address = "Model Town Lahore" }
        });

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, Message, default);

        var saved = await db.Customers.SingleAsync(c => c.Name == "Muhammad Ali");
        Assert.Equal("Model Town Lahore", saved.Address);
        Assert.Null(saved.Phone);
        Assert.Contains(_sent, m => m.Contains("Muhammad Ali") && m.Contains("customer list"));
        Assert.DoesNotContain(_sent, m => m.Contains("samajh nahi aaya"));
    }

    [Fact]
    public async Task ClientAlreadySaved_IsNotDuplicated()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        AnalysisIs(new AiMessageAnalysis
        {
            Intent = "add_customer",
            NewCustomer = new AiNewCustomer { Name = "Muhammad Ali", Address = "Model Town Lahore" }
        });
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, Message, default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, Message, default);

        Assert.Equal(1, await db.Customers.CountAsync(c => c.Name == "Muhammad Ali"));
        Assert.Contains(_sent, m => m.Contains("pehle se customer list mein"));
    }

    [Fact]
    public async Task ImplausiblePhone_IsDropped_AndTheCustomerStillSaved()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        AnalysisIs(new AiMessageAnalysis
        {
            Intent = "add_customer",
            NewCustomer = new AiNewCustomer { Name = "Sara", Phone = "030011122" }
        });

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "Sara, phone 030011122", default);

        var saved = await db.Customers.SingleAsync(c => c.Name == "Sara");
        Assert.Null(saved.Phone);
    }

    [Fact]
    public async Task ClientNamedAtTheShopNameStep_IsSaved_AndTheNameIsAskedAgain()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein");
        AnalysisIs(new AiMessageAnalysis { Intent = "add_customer", NewCustomer = new AiNewCustomer { Name = "Muhammad Ali", City = "Lahore" } });

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, Message, default);

        // The plain-rule reader catches this sentence first (address and name); the model is only the fallback for looser wording.
        Assert.True(await db.Customers.AnyAsync(c => c.Name == "Muhammad Ali" && c.Address == "Model Town Lahore"));
        Assert.Contains(_sent, m => m.Contains("customer list") && m.Contains("dukaan ka naam"));
        Assert.Null((await db.Sellers.SingleAsync()).BusinessName);
    }

    [Fact]
    public async Task ProductLineAtTheCityStep_IsSaved_AndTheCityIsAskedAgain()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections");

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "Brownies Box - 1200", default);

        Assert.True(await db.Products.AnyAsync(p => p.Name == "Brownies Box" && p.Price == 1200));
        Assert.Contains(_sent, m => m.Contains("Brownies Box") && m.Contains("shehar"));
        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlainCityAnswer_DoesNotCostAnAiCall()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections");

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "Lahore", default);

        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OrderWithANamedBuyerButNoPhone_AtTheProductStep_IsTakenAsAnOrder()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        AnalysisIs(new AiMessageAnalysis
        {
            Intent = "new_order",
            IsOrderAttempt = true,
            Order = new AiOrderDraft { CustomerName = "Ayesha", Items = { new AiOrderItemDraft { ProductName = "Kurti", Quantity = 2 } } }
        });

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "Ayesha ko 2 Kurti bhej do", default);

        Assert.True((await db.Sellers.SingleAsync()).OnboardingComplete);
        Assert.DoesNotContain(_sent, m => m.Contains("samajh nahi aaya"));
    }

    private static AiMessageAnalysis OrderFor(string buyer) => new()
    {
        Intent = "new_order",
        IsOrderAttempt = true,
        Order = new AiOrderDraft { CustomerName = buyer, Items = { new AiOrderItemDraft { ProductName = "Kurti", Quantity = 1 } } }
    };

    [Fact]
    public async Task ShortOrderNamingACatalogProduct_AtTheCityStep_IsTakenAsAnOrder()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Kurti - 1800");
        AnalysisIs(OrderFor("Ali"));

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "Ali Kurti", default);

        Assert.True((await db.Sellers.SingleAsync()).OnboardingComplete);
    }

    [Fact]
    public async Task ShortOrderWithAnOrderWord_AtTheShopNameStep_IsTakenAsAnOrder()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein");
        AnalysisIs(OrderFor("Ali"));

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "Ali ko Kurti", default);

        Assert.True((await db.Sellers.SingleAsync()).OnboardingComplete);
    }

    [Theory]
    [InlineData("Model Town Lahore")]
    [InlineData("Ayesha Collections")]
    [InlineData("Lahore")]
    public async Task CityOrShopName_EvenWithAProductInTheCatalog_DoesNotCostAnAiCall(string answer)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Kurti - 1800");

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, answer, default);

        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The exact voice note from the live chat: spoken at the add-product step, transcribed in Urdu script, rewritten by the model.
    // The speech model and the rewrite are mocked here, so this checks the engine's path, not what the real model says.
    [Fact]
    public async Task VoiceNote_NewClientWithoutOrder_AtTheProductStep_IsSavedAsACustomer()
    {
        const string transcript = "میرے پاس نا ایک نیا کلائنٹ آیا ٹھیک ہے؟ جس کا نا ابھی آرڈر تو نہیں اس نے دیا لیکن اس نے نا اپنا دیا ہے جس کا ڈیٹیل وغیرہ نا پروفائل۔ تو اس کا نام ہے محمد علی اور فون وہ بعد میں دے گا جبکہ اس نے ایڈریس دیا ہے ماڈل ٹاؤن لاہور کے۔ تو یہ ایڈ کر دیں از کلائنٹ۔";
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);

        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync("voice-1", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), "audio/ogg", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(transcript);
        var voice = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: media.Object, transcriber: transcriber.Object);
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "Muhammad Ali, address Model Town Lahore, phone baad mein" }, Actions = { "reply" } });
        AnalysisIs(new AiMessageAnalysis
        {
            Intent = "add_customer",
            NewCustomer = new AiNewCustomer { Name = "Muhammad Ali", Address = "Model Town Lahore" }
        });

        await voice.HandleAudioMessageAsync(Phone, "voice-1");

        var saved = await db.Customers.SingleAsync(c => c.Name == "Muhammad Ali");
        Assert.Equal("Model Town Lahore", saved.Address);
        Assert.Null(saved.Phone);
        Assert.Contains(_sent, m => m.Contains("Samjha"));
        Assert.Contains(_sent, m => m.Contains("Muhammad Ali") && m.Contains("customer list"));
        Assert.DoesNotContain(_sent, m => m.Contains("samajh nahi aaya"));
    }

    private void AiMustNotBeAsked() =>
        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task CustomerAddCommand_AtTheProductStep_SavesWithoutTheAi()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "naya client hai Muhammad Ali, address Model Town Lahore, phone baad mein", default);

        var saved = await db.Customers.SingleAsync(c => c.Name == "Muhammad Ali");
        Assert.Equal("Model Town Lahore", saved.Address);
        Assert.Null(saved.Phone);
        Assert.Contains(_sent, m => m.Contains("Muhammad Ali") && m.Contains("customer list"));
        AiMustNotBeAsked();
    }

    // The second live voice note (spoken as a sentence, "ایڈریس" for address): it used to be read as "rename a customer" and refused at this step.
    [Fact]
    public async Task SpokenCustomerSentence_AtTheProductStep_SavesWithoutTheAi_NotRefusedAsAnOrder()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);

        await CreateEngine(db).HandleIncomingMessageAsync(Phone,
            "میرا کسٹمر ہے جس کا نام محمد علی ہے۔ فون تو چلیں میں بعد میں بتاؤں گا، اس کا ایڈریس ہے ماڈل ٹاؤن لاہور۔", default);

        var saved = await db.Customers.SingleAsync(c => c.Name == "محمد علی");
        Assert.Equal("ماڈل ٹاؤن لاہور", saved.Address);
        Assert.Null(saved.Phone);
        Assert.Contains(_sent, m => m.Contains("customer list"));
        Assert.DoesNotContain(_sent, m => m.Contains("order darj nahi ho sakta"));
        AiMustNotBeAsked();
    }

    [Fact]
    public async Task SpokenCustomerWithoutDetails_AtTheProductStep_IsSaved_WhenTheSpeechModelWritesSarif()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "میرے پاس ایک نیا صارفہ ہے جس کا نام محمد علی", default);

        Assert.True(await db.Customers.AnyAsync(c => c.Name == "محمد علی"));
        Assert.DoesNotContain(_sent, m => m.Contains("order darj nahi ho sakta"));
        AiMustNotBeAsked();
    }

    [Fact]
    public async Task CustomerAddCommand_AtTheCityStep_SavesWithoutTheAi_AndAsksTheCityAgain()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections");

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "customer add Sara, Model Town, 03001234567", default);

        var saved = await db.Customers.SingleAsync(c => c.Name == "Sara");
        Assert.Equal("03001234567", saved.Phone);
        Assert.Contains(_sent, m => m.Contains("customer list") && m.Contains("shehar"));
        AiMustNotBeAsked();
    }

    [Fact]
    public async Task CustomerAddCommand_AtTheShopNameStep_NeedsDetails_ABareNameIsTheShop()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein");
        AnalysisIs(new AiMessageAnalysis { Intent = "unclear" }); // "New Customer Care" has an order / customer word, so the AI is asked once and says it is nothing
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "customer add Ali, Lahore", default);
        Assert.True(await db.Customers.AnyAsync(c => c.Name == "Ali"));
        Assert.Null((await db.Sellers.SingleAsync()).BusinessName);

        await engine.HandleIncomingMessageAsync(Phone, "New Customer Care", default);
        Assert.Equal("New Customer Care", (await db.Sellers.SingleAsync()).BusinessName);
        Assert.Equal(1, await db.Customers.CountAsync());
    }

    [Fact]
    public async Task CustomerAddCommand_WhenIdle_SavesWithoutTheAi_AndOffersTheOrder()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardUntilAsync(db, "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Kurti - 1800", "done");

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "customer add Sara, Model Town, 03001234567", default);

        var saved = await db.Customers.SingleAsync(c => c.Name == "Sara");
        Assert.Equal("Model Town", saved.Address);
        Assert.Equal("03001234567", saved.Phone);
        Assert.Contains(_sent, m => m.Contains("Sara") && m.Contains("customer list"));
        AiMustNotBeAsked();
    }

    // The live note at 11:03: "آج کے روزل آرسرز دکھائیں" (the speech model wrote "روزل آرسرز" for "آرڈرز"). The rewrite was a "command" step "orders today",
    // which the setup add-product state did not allow, so the step was dropped and the note answered "not clear".
    [Fact]
    public async Task SpokenTodaysOrders_AtTheProductStep_RunsTheReadOnlyCommand_NotRefusedAsUnclear()
    {
        const string transcript = "آج کے روزل آرسرز دکھائیں";
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync("voice-2", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), "audio/ogg", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(transcript);
        var voice = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: media.Object, transcriber: transcriber.Object);
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "orders today" }, Actions = { "command" }, Spans = { "آج کے آرسرز دکھائیں" } });

        await voice.HandleAudioMessageAsync(Phone, "voice-2");

        Assert.DoesNotContain(_sent, m => m.Contains("saaf nahi thi"));
        Assert.DoesNotContain(_sent, m => m.Contains("order darj nahi ho sakta"));
        Assert.Contains(_sent, m => m.Contains("Samjha") && m.Contains("orders today"));
    }

    [Fact]
    public async Task OwnStockStatement_AtTheProductStep_IsNotTakenAsAnOrder()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        AnalysisIs(new AiMessageAnalysis
        {
            Intent = "new_order",
            IsOrderAttempt = true,
            Order = new AiOrderDraft { Items = { new AiOrderItemDraft { ProductName = "Lawn Suit", Quantity = 4 } } }
        });

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "mere paas 4 lawn suit hain, 3500", default);

        Assert.False((await db.Sellers.SingleAsync()).OnboardingComplete);
    }

    public void Dispose() => _dbFactory.Dispose();
}
