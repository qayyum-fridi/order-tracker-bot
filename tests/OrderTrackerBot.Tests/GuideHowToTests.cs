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

// Guide, how-to and contact-support questions go through the intent catalog: a "how do I ...?" is answered with the line to type (never run),
// "contact support" is recorded and answered with next steps, and a seller back after 30 days is welcomed once.
public class GuideHowToTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IFounderAlertNotifier> _founder = new();
    private readonly List<string> _text = new();

    public GuideHowToTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private ConversationEngine Engine(AppDbContext db, SupportOptions? support = null) =>
        new(db, _ai.Object, _sender.Object, _founder.Object, features: new FeatureOptions { ShortcutButtons = false }, support: support);

    private static async Task<Seller> SeedAsync(AppDbContext db)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        return seller;
    }

    private string Replies => string.Join("\n---\n", _text);

    // ---- how-to questions

    [Theory]
    [InlineData("how do we add a staff member?")]
    [InlineData("staff member kaise add karte hain")]
    [InlineData("naya mulazim kaise add karun")]
    [InlineData("اسٹاف ممبر کیسے شامل کروں")]
    public async Task HowToAddStaff_IsAnsweredWithTheLineToType_AndAnExample_AndSavesNothing(string question)
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, question, default);

        Assert.Contains("staff add [name]", Replies);
        Assert.Contains("staff add Ali 03001234567 driver", Replies);
        Assert.DoesNotContain("samajh nahi aaya", Replies);
        Assert.Equal(0, await db.StaffMembers.CountAsync());
        // The closest command is found from the catalog itself: no model call is needed.
        _ai.Verify(a => a.RouteIntentAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Seen live: "Custom attribute product ma kasa sath save kar saktha han" got the generic Products taps.
    [Theory]
    [InlineData("Custom attribute product ma kasa sath save kar saktha han")]
    [InlineData("custom attribute product mein kaise save karun")]
    [InlineData("how can I save a custom attribute with a product")]
    public async Task HowToSaveACustomAttribute_ShowsTheProductLineWithAnAttribute_AndSavesNothing(string question)
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, question, default);

        Assert.Contains("[name] - [price], [attribute]: [attribute_value]", Replies);
        Assert.Contains("Polo Shirt - 500, fabric: cotton", Replies);
        Assert.DoesNotContain("samajh nahi", Replies);
        Assert.Equal(0, await db.Products.CountAsync());
        _ai.Verify(a => a.RouteIntentAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Seen live: "Yar mughy order ko edit kana ka tarika bato" answered "tareeqa nahi mila" (typos, no close command).
    [Theory]
    [InlineData("order edit kaise karte hain")]
    [InlineData("how do I edit an order")]
    public async Task HowToEditAnOrder_ShowsTheEditLine(string question)
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, question, default);

        Assert.DoesNotContain("tareeqa nahi mila", Replies);
        Assert.Contains("edit", Replies, StringComparison.OrdinalIgnoreCase);
    }

    // Typos and slang around a how-to word: whatever is offered, it is an answer, never "tareeqa nahi mila".
    [Fact]
    public async Task HowToWithTyposAndSlang_IsAnswered_NotNothingFound()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "Yar mughy order ko edit kana ka tarika bato", default);

        Assert.DoesNotContain("tareeqa nahi mila", Replies);
        Assert.NotEmpty(_text);
    }

    [Theory]
    [InlineData("kasa", "kaise")]
    [InlineData("kesa", "kese")]
    [InlineData("kaisey", "kaisay")]
    [InlineData("kesay", "kese")]
    public void NearSpellings_AreTheSameHowToWord(string typed, string keyword) => Assert.True(IntentKeywords.IsNear(typed, keyword));

    [Theory]
    [InlineData("karne", "kaise")]   // different consonants
    [InlineData("kisi", "kaise")]
    [InlineData("kaise", "kaise")]   // exact, not a typo
    [InlineData("kar", "kaise")]     // too short
    public void OtherWords_AreNotNearSpellings(string typed, string keyword) => Assert.False(IntentKeywords.IsNear(typed, keyword));

    [Fact]
    public async Task HowToQuestion_WithAnUnreadableWord_GuessesTheClosestCommand_InsteadOfNothingFound()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "staff mmber kaise add krte hain", default);

        Assert.DoesNotContain("tareeqa nahi mila", Replies);
        Assert.Contains("staff add", Replies);
    }

    // The real cause of that live reply: a customer word plus a bare "karo" was read as "create a customer" (a form that is off), not as a search.
    [Theory]
    [InlineData("Ali koi customers ha mera pass تلاش karo")]
    [InlineData("customer Ali talash karo")]
    [InlineData("Ali naam ka customer dhoondo")]
    public void SearchSentences_WithACustomerWord_AreNotCreateCustomerHelp(string message) =>
        Assert.NotEqual(CommandKind.DetailedForm, CommandParser.TryParse(message)?.Kind);

    // Seen live: "Ali koi customers ha mera pass تلاش karo" went to loyalty ("feature not available").
    [Fact]
    public async Task CustomerSearchWords_AreCatalogKeywords_AndTheRoutedSearchRuns()
    {
        var search = IntentCatalog.Find("customer.search")!;
        Assert.Contains("talash", search.Keywords!);
        Assert.Contains("تلاش", search.Keywords!);

        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Ali Khan", Phone = "03001234567" });
        await db.SaveChangesAsync();
        _ai.Setup(a => a.RouteIntentAsync("Ali koi customers ha mera pass تلاش karo", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("customer.search", new Dictionary<string, string> { ["name"] = "Ali" }, 0.95));

        await Engine(db).HandleIncomingMessageAsync(Phone, "Ali koi customers ha mera pass تلاش karo", default);

        Assert.Contains("Ali Khan", Replies);
        Assert.DoesNotContain("available nahi", Replies);
    }

    // Seen live: "Mera kis kam ki yah application ha" offered "low stock".
    [Fact]
    public async Task WhatIsThisAppFor_RoutedToTheGuide_IsAnswered_NotAskedAgain()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync("Mera kis kam ki yah application ha", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("account.guide", new Dictionary<string, string>(), 0.95));

        await Engine(db).HandleIncomingMessageAsync(Phone, "Mera kis kam ki yah application ha", default);

        Assert.NotEmpty(_text);
        Assert.DoesNotContain("samajh nahi", Replies);
        Assert.Contains("what is this app for", IntentCatalog.Find("account.guide")!.Examples);
    }

    // Seen live: "Customers support ka document do" showed the customer list ("customers" alone claimed it).
    [Fact]
    public async Task CustomersSupport_IsSupport_NotTheCustomerList()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001234567" });
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, "Customers support ka document do", default);

        Assert.DoesNotContain("Aapke Customers", Replies);
        Assert.NotEmpty(_text);
    }

    [Fact]
    public async Task PlainCustomers_StillShowsTheCustomerList()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001234567" });
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, "customers", default);

        Assert.Contains("Aapke Customers", Replies);
    }

    [Fact]
    public async Task CustomerList_UrduName_IsIsolated_AndTheLineStaysLeftToRight()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "محمد سجاد", Phone = "03001234567" });
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03007654321" });
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, "customers", default);

        Assert.Matches("‎\\d ⁨محمد سجاد⁩ - 03001234567 - 0 orders", Replies);
        Assert.Matches(@"\d Sara - 03007654321 - 0 orders", Replies); // Latin names are left untouched
    }

    [Theory]
    [InlineData("kasa")]
    [InlineData("kaisey")]
    [InlineData("kesay")]
    public void HowToSpellings_AreHowToQuestions(string word)
    {
        var howTo = IntentCatalog.Find(IntentCatalog.HowToId)!;
        Assert.NotEmpty(IntentKeywords.MatchTolerant($"staff member {word} add karte hain", new[] { howTo }));
    }

    [Fact]
    public async Task HowToQuestion_IsNeverRun_EvenWhenTheMessageNamesTheValues()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "how do I add expense 500 petrol", default);

        Assert.Equal(0, await db.Expenses.CountAsync());
        Assert.Contains("expense", Replies, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HowToQuestion_TheModelPlacedOnAnAction_ShowsTheLine_AndDoesNotRunIt()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync("kaise likhte hain jaldi se zara", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("expense.add", new Dictionary<string, string> { ["amount"] = "500", ["note"] = "petrol" }, 0.95));

        await Engine(db).HandleIncomingMessageAsync(Phone, "kaise likhte hain jaldi se zara", default);

        Assert.Equal(0, await db.Expenses.CountAsync());
        Assert.Contains("expense", Replies, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HowToQuestion_NothingInTheCatalogMatches_SaysWhereToLook_NotNotUnderstood()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "how do I teleport to the moon", default);

        Assert.Contains("\"guide\"", Replies);
        Assert.Contains("\"support\"", Replies);
        Assert.DoesNotContain("samajh nahi aaya", Replies);
    }

    [Fact]
    public async Task HowToQuestion_ByVoice_GetsTheSameAnswer()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync("voice-1", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[2500], "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), "audio/ogg", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("how do we add a staff member?");
        var voice = new ConversationEngine(db, _ai.Object, _sender.Object, _founder.Object, media: media.Object, transcriber: transcriber.Object,
            features: new FeatureOptions { ShortcutButtons = false });

        await voice.HandleAudioMessageAsync(Phone, "voice-1");

        // The answer may go out as text or as a question with taps: look at everything the sender was given.
        var sent = string.Join("\n", _sender.Invocations.SelectMany(i => i.Arguments).OfType<string>());
        Assert.Contains("staff add [name]", sent);
        Assert.Equal(0, await db.StaffMembers.CountAsync());
    }

    [Fact]
    public async Task StaffRemove_FromTheModel_IsShownAsTheLineToType_NotRun()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        db.StaffMembers.Add(new StaffMember { SellerId = seller.Id, Name = "Ali" });
        await db.SaveChangesAsync();
        _ai.Setup(a => a.RouteIntentAsync("Ali ab nahi aata", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("staff.remove", new Dictionary<string, string> { ["name"] = "Ali" }, 0.95));

        await Engine(db).HandleIncomingMessageAsync(Phone, "Ali ab nahi aata", default);

        Assert.Contains("staff remove Ali", Replies);
        Assert.Equal(1, await db.StaffMembers.CountAsync(s => s.IsActive));
    }

    // ---- contact support

    private static readonly SupportOptions Configured = new() { WhatsAppNumber = "+92 300 0000000", Email = "help@example.test", Hours = "Mon-Sat 10am-6pm" };

    [Fact]
    public async Task ContactSupport_IsRecorded_TheFounderIsAlerted_AndTheSellerGetsNextSteps()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync("mujhe support se baat karni hai", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("support.contact", new Dictionary<string, string>(), 0.9));

        await Engine(db, Configured).HandleIncomingMessageAsync(Phone, "mujhe support se baat karni hai", default);

        Assert.Equal(1, await db.MerchantFeedbacks.CountAsync(f => f.Text.StartsWith("[support request]")));
        _founder.Verify(f => f.NotifyAsync(It.IsAny<int>(), It.Is<string>(m => m.Contains("[support request]") && m.Contains("Ayesha Collections")), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("hamari team", Replies);
        Assert.Contains("+92 300 0000000", Replies);
        Assert.Contains("help@example.test", Replies);
        Assert.Contains("Mon-Sat 10am-6pm", Replies);
    }

    [Fact]
    public async Task ContactSupport_WithNoContactConfigured_ShowsNoDetails_ButStillRecords()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "contact support", default);

        Assert.DoesNotContain("📞", Replies);
        Assert.DoesNotContain("🕐", Replies);
        Assert.Equal(1, await db.MerchantFeedbacks.CountAsync());
    }

    [Fact]
    public async Task ContactSupport_Topic_GoesToTheFounder()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "contact support order save nahi ho raha", default);

        _founder.Verify(f => f.NotifyAsync(It.IsAny<int>(), It.Is<string>(m => m.Contains("order save nahi ho raha")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ContactSupport_Twice_AlertsTheFounderOnce_AndTellsTheSellerItIsAlreadyWithTheTeam()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db, Configured);

        await engine.HandleIncomingMessageAsync(Phone, "support", default);
        _text.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "support", default);

        _founder.Verify(f => f.NotifyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(1, await db.MerchantFeedbacks.CountAsync());
        Assert.Contains("pehle se", Replies);
        Assert.Contains("+92 300 0000000", Replies);
    }

    [Fact]
    public async Task HowDoIContactSupport_RunsTheContactRequest_NotAHowToLine()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db, Configured).HandleIncomingMessageAsync(Phone, "how do I contact support?", default);

        _founder.Verify(f => f.NotifyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("hamari team", Replies);
    }

    [Fact]
    public async Task ContactSupport_StillWorks_WhenTheTrialHasEnded()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        seller.TrialEndsAt = DateTime.UtcNow.AddDays(-5);
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founder.Object, billing: new BillingOptions { Enabled = true },
            features: new FeatureOptions { ShortcutButtons = false });

        await engine.HandleIncomingMessageAsync(Phone, "contact support", default);

        _founder.Verify(f => f.NotifyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- welcome back

    private static async Task LogInboundAsync(AppDbContext db, DateTime at)
    {
        db.MessageLogs.Add(new MessageLog { Phone = Phone, Direction = "inbound", RawText = "orders today", CreatedAt = at });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SellerBackAfter30Days_GetsOneWelcome_ThenTheNormalReply_AndNotAgain()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        await LogInboundAsync(db, DateTime.UtcNow.AddDays(-31));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "menu", default);
        var firstTurn = _text.ToList();
        _text.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "menu", default);

        Assert.Contains("Khush amdeed", firstTurn[0]);       // the greeting comes first, as a reply to the message just sent
        Assert.Equal(1, firstTurn.Count(t => t.Contains("Khush amdeed")));
        Assert.DoesNotContain("Khush amdeed", Replies);       // once per gap
    }

    [Theory]
    [InlineData(29)]
    [InlineData(1)]
    public async Task SellerBackBeforeTheGap_IsNotWelcomed(int daysAgo)
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        await LogInboundAsync(db, DateTime.UtcNow.AddDays(-daysAgo));

        await Engine(db).HandleIncomingMessageAsync(Phone, "menu", default);

        Assert.DoesNotContain("Khush amdeed", Replies);
    }

    [Fact]
    public async Task FirstMessageEver_IsNotWelcomedBack()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "menu", default);

        Assert.DoesNotContain("Khush amdeed", Replies);
    }

    [Fact]
    public async Task WelcomeBack_IsOnlyEverAReply_NeverSentByTheScheduler()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        await LogInboundAsync(db, DateTime.UtcNow.AddDays(-45));

        await Engine(db).RunScheduledJobsAsync(DateTime.UtcNow, default);

        Assert.DoesNotContain("Khush amdeed", Replies);
    }

    public void Dispose() => _dbFactory.Dispose();
}
