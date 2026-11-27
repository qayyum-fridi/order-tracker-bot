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

// Parking (a new voice request while a YES / tap is open is kept and runs once that is answered) and spoken summaries (a list or a long
// reply to a voice note gets a short voice version). The model's answers are scripted, so no key is needed.
public class ParkAndSpokenTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    private readonly List<string> _spoken = new();
    private readonly Dictionary<string, TurnAction> _model = new();
    private string _transcript = "";
    private int _notes, _saraId, _bilalId;

    public void Dispose() => _dbFactory.Dispose();

    private async Task<(AppDbContext Db, Func<string, Task> Type, Func<string, Task> Speak)> ShopAsync()
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", BusinessType = "Clothing", OnboardingComplete = true,
            Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var shirt = new Product { SellerId = seller.Id, Name = "Shirt", Price = 1800 };
        db.Products.Add(shirt);
        var sara = new Customer { SellerId = seller.Id, Name = "Sara Khan", Phone = "03004445566" };
        var bilal = new Customer { SellerId = seller.Id, Name = "Bilal Ahmed", Phone = "03215557788" };
        db.Customers.AddRange(sara, bilal);
        await db.SaveChangesAsync();
        (_saraId, _bilalId) = (sara.Id, bilal.Id);
        foreach (var (customer, hoursAgo) in new[] { (sara, 5), (bilal, 2) })
        {
            var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, CreatedAt = DateTime.UtcNow.AddHours(-hoursAgo), Subtotal = 1800, Total = 1800 };
            order.Items.Add(new OrderItem { ProductId = shirt.Id, ProductNameSnapshot = "Shirt", UnitPrice = 1800, Quantity = 1 });
            db.Orders.Add(order);
        }
        await db.SaveChangesAsync();

        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _sent.Add(b)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendVoiceNoteAsync(Phone, It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var older = new Mock<IAiOrderAssistant>();
        older.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
        var turn = new Mock<ITurnInterpreter>();
        turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), It.IsAny<string>(), It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TurnContext _, string text, AiImageInput? _, CancellationToken _) => _model.GetValueOrDefault(text));
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ((byte[] Bytes, string MimeType)?)(System.Text.Encoding.UTF8.GetBytes($"note-{++_notes}" + new string('.', 4096)), "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.Setup(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _transcript);
        var speech = new Mock<ISpeechSynthesizer>();
        speech.Setup(s => s.IsConfigured).Returns(true);
        speech.Setup(s => s.SynthesizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((t, _) => _spoken.Add(t)).ReturnsAsync(new byte[] { 1, 2, 3 });
        var features = new FeatureOptions { ModelFirst = "on", MvpOnly = true, SpokenReplies = true, ShortcutButtons = false, SuggestionButtons = false };

        ConversationEngine Engine() => new(db, older.Object, sender.Object, Mock.Of<IFounderAlertNotifier>(), media: media.Object, features: features,
            transcriber: transcriber.Object, turn: turn.Object, speech: speech.Object);
        return (db,
            async text => await Engine().HandleIncomingMessageAsync(Phone, text, default),
            async transcript => { _transcript = transcript; await Engine().HandleAudioMessageAsync(Phone, $"media-{_notes}", $"wamid-{_notes}-{transcript.Length}"); });
    }

    private static async Task<int> OrderOf(AppDbContext db, int customerId) => (await db.Orders.AsNoTracking().SingleAsync(o => o.CustomerId == customerId)).Id;

    // ----- Parking -----

    [Fact]
    public async Task ANewVoiceRequest_WhileAYesIsOpen_IsKept_TheOpenQuestionIsAnsweredFirst_ThenItRuns()
    {
        var (db, type, speak) = await ShopAsync();
        using var _ = db;
        var saraOrder = await OrderOf(db, _saraId);
        // The bot is waiting for YES to mark Sara's order shipped.
        var session = await db.Sessions.SingleAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.ModelStatusOrderId = saraOrder;
        ctx.ModelStatusValue = "shipped";
        session.ContextJson = ctx.ToJson();
        session.State = ConversationState.AwaitingModelStatusConfirmation;
        await db.SaveChangesAsync();
        const string note = "acha suno Bilal ka number ab 03001112222 kar do";
        _model[note] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Bilal", Field = "phone", Value = "03001112222" };

        await speak(note);
        Assert.Contains(_sent, m => m.StartsWith(MessageTemplates.Get("park.001", "").Split('\n')[0]));
        Assert.Equal(ConversationState.AwaitingModelStatusConfirmation, (await db.Sessions.AsNoTracking().SingleAsync()).State);   // the open question stays
        Assert.Equal("03215557788", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _bilalId)).Phone);

        await type("yes");   // the open question: Sara's order shipped
        Assert.Equal(OrderStatus.Shipped, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == saraOrder)).Status);
        Assert.Contains(_sent, m => m.Contains("Bilal Ahmed") && m.Contains("03001112222"));   // the kept request now asks its YES

        await type("yes");
        Assert.Equal("03001112222", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _bilalId)).Phone);
    }

    /// <summary>The bot waits for YES to mark Sara's order shipped.</summary>
    private async Task<int> WaitForShippedYesAsync(AppDbContext db)
    {
        var saraOrder = await OrderOf(db, _saraId);
        var session = await db.Sessions.SingleAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.ModelStatusOrderId = saraOrder;
        ctx.ModelStatusValue = "shipped";
        session.ContextJson = ctx.ToJson();
        session.State = ConversationState.AwaitingModelStatusConfirmation;
        await db.SaveChangesAsync();
        return saraOrder;
    }

    [Fact]
    public async Task ATypedNewRequest_WhileAYesIsOpen_IsKept_NotReadAsNo_ThenRunsAfterTheAnswer()
    {
        var (db, type, _) = await ShopAsync();
        using var _db = db;
        var saraOrder = await WaitForShippedYesAsync(db);
        const string message = "acha suno Bilal ka number ab 03001112222 kar do";
        _model[message] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Bilal", Field = "phone", Value = "03001112222" };

        await type(message);
        Assert.Contains(_sent, m => m.StartsWith(MessageTemplates.Get("park.001", "").Split('\n')[0]));
        Assert.Equal(ConversationState.AwaitingModelStatusConfirmation, (await db.Sessions.AsNoTracking().SingleAsync()).State);
        Assert.Equal(OrderStatus.Pending, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == saraOrder)).Status);   // not taken as a NO either

        await type("yes");
        Assert.Equal(OrderStatus.Shipped, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == saraOrder)).Status);
        Assert.Contains(_sent, m => m.Contains("Bilal Ahmed") && m.Contains("03001112222"));

        await type("yes");
        Assert.Equal("03001112222", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _bilalId)).Phone);
    }

    [Fact]
    public async Task ATypedYesWord_IsTheAnswer_NotParked()
    {
        var (db, type, _) = await ShopAsync();
        using var _db = db;
        var saraOrder = await WaitForShippedYesAsync(db);

        await type("haan");

        Assert.DoesNotContain(_sent, m => m.StartsWith(MessageTemplates.Get("park.001", "").Split('\n')[0]));
        Assert.Equal(OrderStatus.Shipped, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == saraOrder)).Status);
    }

    /// <summary>The bot waits for a 1 / 2 pick ("which order?"), asked <paramref name="askedMinutesAgo"/> minutes ago.</summary>
    private async Task WaitForPickAsync(AppDbContext db, int askedMinutesAgo)
    {
        var session = await db.Sessions.SingleAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.ClarificationOptions = new List<string> { "Sara Khan ka order", "Bilal Ahmed ka order" };
        ctx.PickAskedAt = DateTime.UtcNow.AddMinutes(-askedMinutesAgo);
        session.ContextJson = ctx.ToJson();
        session.State = ConversationState.AwaitingClarificationChoice;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ANewVoiceRequest_WhileAPickQuestionIsOpen_IsKept_AndTheOptionsAreShownAgain()
    {
        var (db, _, speak) = await ShopAsync();
        using var _db = db;
        await WaitForPickAsync(db, askedMinutesAgo: 1);
        const string note = "acha suno Bilal ka number ab 03001112222 kar do";
        _model[note] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Bilal", Field = "phone", Value = "03001112222" };

        await speak(note);

        Assert.Contains(_sent, m => m.StartsWith(MessageTemplates.Get("park.001", "").Split('\n')[0]));
        Assert.Contains(_sent, m => m == MessageTemplates.Get("park.002"));   // the open question comes back, with its two options as taps
        Assert.Equal(ConversationState.AwaitingClarificationChoice, (await db.Sessions.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task APickQuestionOlderThanTenMinutes_IsDropped_SoANewVoiceRequestRunsInsteadOfBeingParked()
    {
        var (db, _, speak) = await ShopAsync();
        using var _db = db;
        await WaitForPickAsync(db, askedMinutesAgo: 11);
        const string note = "acha suno Bilal ka number ab 03001112222 kar do";
        _model[note] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Bilal", Field = "phone", Value = "03001112222" };

        await speak(note);

        Assert.DoesNotContain(_sent, m => m.StartsWith(MessageTemplates.Get("park.001", "").Split('\n')[0]));
        Assert.Contains(_sent, m => m.Contains("Bilal Ahmed") && m.Contains("03001112222"));   // the request itself ran: it asks its YES
        var ctx = SessionContextData.FromJson((await db.Sessions.AsNoTracking().SingleAsync()).ContextJson);
        Assert.Null(ctx.ClarificationOptions);
        Assert.Null(ctx.PickAskedAt);
    }

    [Theory]
    [InlineData("haan", true)]
    [InlineData("nahi", true)]
    [InlineData("2", true)]
    [InlineData("doosra wala", true)]
    [InlineData("acha suno Bilal ka number ab 03001112222 kar do", false)]
    [InlineData("Sara ko do shirt aur bhejni hain", false)]
    public void AShortReplyIsAnAnswer_ASentenceIsANewRequest(string message, bool answer) =>
        Assert.Equal(answer, ConversationEngine.LooksLikeAnswer(message));

    // ----- Spoken summaries -----

    [Fact]
    public void AShortReply_IsReadInFull_WithoutEmoji()
    {
        Assert.Equal("Order #3 save ho gaya.\nShukriya!", ConversationEngine.SpokenVersion("✅ Order #3 *save* ho gaya.\n🙏 Shukriya!"));
    }

    [Fact]
    public void AList_IsSummedUp_WithoutEmojiOrFormatting()
    {
        var text = "❓ *Kaun sa order* cancel karna hai? Neeche se chunein 👇\n\n" +
                   string.Join("\n", Enumerable.Range(1, 6).Select(i => $"{i}️⃣ #{i} Customer {i} — 1 Shirt, 2 Dupatta, 1 Lawn Suit (pending, Rs.4,000)"));

        var spoken = ConversationEngine.SpokenVersion(text);

        Assert.Equal(MessageTemplates.Get("spoken.list", "Kaun sa order cancel karna hai? Neeche se chunein", 6), spoken);
        Assert.DoesNotContain("❓", spoken);
        Assert.DoesNotContain("*", spoken);
    }

    [Fact]
    public void ALongReplyWithoutAList_GetsItsFirstLine_AndAPointerToTheText()
    {
        var text = "📊 Aaj ka hisaab: 12 orders, Rs.45,000 ki sale.\n" + string.Join("\n", Enumerable.Repeat("Tafseel ki ek lambi line jo text mein rehni chahiye aur parhi nahi jaye gi.", 6));

        Assert.Equal(MessageTemplates.Get("spoken.more", "Aaj ka hisaab: 12 orders, Rs.45,000 ki sale."), ConversationEngine.SpokenVersion(text));
    }

    [Fact]
    public async Task AVoiceNote_ThatGetsAListOfChoices_AlsoGetsASpokenSummary()
    {
        var (db, _, speak) = await ShopAsync();
        using var _db = db;
        const string note = "ek order cancel karna hai please";
        _model[note] = new TurnAction { Action = TurnAction.CancelOrder };

        await speak(note);

        // Two choices are short enough to read in full; a longer list is summed up (AList_IsSummedUp_WithoutEmojiOrFormatting).
        var said = Assert.Single(_spoken);
        Assert.StartsWith("Kaun sa order cancel karna hai?", said);
        Assert.Contains("Bilal Ahmed", said);
        Assert.Contains("Sara Khan", said);
        Assert.DoesNotContain("❓", said);
    }

    // ----- A "nahi / mat" in the message holds a write that would run without a YES -----

    private const string NegatedStock = "shirt 5 aur nahi aayi";
    private static TurnAction ShirtStock() => new() { Action = TurnAction.AddStock, Products = { new AiNewProduct { Name = "Shirt", Stock = 5 } } };
    private static async Task<int?> ShirtStockAsync(AppDbContext db) => (await db.Products.AsNoTracking().SingleAsync(p => p.Name == "Shirt")).StockQty;

    [Fact]
    public async Task AStockWrite_FromAMessageThatSaysNahi_WaitsForYes_ThenRuns()
    {
        var (db, type, _) = await ShopAsync();
        using var _db = db;
        _model[NegatedStock] = ShirtStock();

        await type(NegatedStock);
        Assert.Null(await ShirtStockAsync(db));   // nothing changed yet
        Assert.Contains(_sent, m => m == MessageTemplates.Get("model-held.001", "Stock barhana"));
        Assert.Equal(ConversationState.AwaitingModelStatusConfirmation, (await db.Sessions.AsNoTracking().SingleAsync()).State);

        await type("yes");
        Assert.Equal(5, await ShirtStockAsync(db));
    }

    [Fact]
    public async Task AStockWrite_FromAMessageThatSaysNahi_AnsweredNo_ChangesNothing()
    {
        var (db, type, _) = await ShopAsync();
        using var _db = db;
        _model[NegatedStock] = ShirtStock();

        await type(NegatedStock);
        await type("no");

        Assert.Null(await ShirtStockAsync(db));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task AStockWrite_WithoutANegation_StillRunsStraightAway()
    {
        var (db, type, _) = await ShopAsync();
        using var _db = db;
        const string message = "shirt 5 aur aa gayi";
        _model[message] = ShirtStock();

        await type(message);

        Assert.Equal(5, await ShirtStockAsync(db));
        Assert.DoesNotContain(_sent, m => m == MessageTemplates.Get("model-held.001", "Stock barhana"));
    }

    [Theory]
    [InlineData("cancel mat karna", true)]
    [InlineData("kurti 5 aur nahi aayi", true)]
    [InlineData("don't add it", true)]
    [InlineData("یہ آرڈر نہیں بھیجنا", true)]
    [InlineData("nai kurti 1500 ki", false)]          // "nai" is also "new"
    [InlineData("kurti ki قیمت 1500", false)]         // "مت" inside "قیمت" is not a word
    [InlineData("sorry, kurti 5 aur aa gayi", false)]
    [InlineData("kurti 5 aur aa gayi", false)]
    public void SaysNo_MatchesWholeNegationWordsOnly(string message, bool expected) => Assert.Equal(expected, ActionRisk.SaysNo(message));

    // ----- An open model YES / NO expires after 10 minutes -----

    private async Task<int> WaitForShippedYesAskedAsync(AppDbContext db, int askedMinutesAgo)
    {
        var saraOrder = await WaitForShippedYesAsync(db);
        var session = await db.Sessions.SingleAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.ModelConfirmAskedAt = DateTime.UtcNow.AddMinutes(-askedMinutesAgo);
        session.ContextJson = ctx.ToJson();
        await db.SaveChangesAsync();
        return saraOrder;
    }

    [Fact]
    public async Task AYes_ToAModelQuestionOlderThanTenMinutes_ChangesNothing()
    {
        var (db, type, _) = await ShopAsync();
        using var _db = db;
        var saraOrder = await WaitForShippedYesAskedAsync(db, askedMinutesAgo: 11);

        await type("yes");

        Assert.NotEqual(OrderStatus.Shipped, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == saraOrder)).Status);
        Assert.Contains(_sent, m => m == MessageTemplates.Get("model-confirm.expired"));
        var session = await db.Sessions.AsNoTracking().SingleAsync();
        Assert.Equal(ConversationState.Idle, session.State);
        var ctx = SessionContextData.FromJson(session.ContextJson);
        Assert.Null(ctx.ModelStatusOrderId);
        Assert.Null(ctx.ModelConfirmAskedAt);
    }

    [Fact]
    public async Task AYes_ToAModelQuestionWithinTenMinutes_StillRuns()
    {
        var (db, type, _) = await ShopAsync();
        using var _db = db;
        var saraOrder = await WaitForShippedYesAskedAsync(db, askedMinutesAgo: 5);

        await type("yes");

        Assert.Equal(OrderStatus.Shipped, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == saraOrder)).Status);
        Assert.DoesNotContain(_sent, m => m == MessageTemplates.Get("model-confirm.expired"));
    }

    [Fact]
    public async Task AModelQuestion_GetsItsAskedTime_WhenItIsOpened()
    {
        var (db, type, _) = await ShopAsync();
        using var _db = db;
        _model[NegatedStock] = ShirtStock();

        await type(NegatedStock);

        var ctx = SessionContextData.FromJson((await db.Sessions.AsNoTracking().SingleAsync()).ContextJson);
        Assert.NotNull(ctx.ModelConfirmAskedAt);
    }
}
