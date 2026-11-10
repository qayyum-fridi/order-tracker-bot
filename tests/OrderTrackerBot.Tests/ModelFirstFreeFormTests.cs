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

// Features:ModelFirst = on: a free-form message from an idle seller is answered from the ONE understanding call (customers, orders, catalog,
// how-to, a question), and never handed to the older keyword router / order reader, which guessed commands from loose words.
public class ModelFirstFreeFormTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new(MockBehavior.Loose);
    private readonly Mock<ITurnInterpreter> _turn = new();
    private readonly List<string> _sent = new();
    private readonly AppDbContext _db;
    private readonly Seller _seller;

    public ModelFirstFreeFormTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _sent.Add(b)).Returns(Task.CompletedTask);
        _db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        _db.Sellers.Add(_seller);
        _db.SaveChanges();
        _db.Products.Add(new Product { SellerId = _seller.Id, Name = "Kurti", Price = 1800 });
        _db.Customers.Add(new Customer { SellerId = _seller.Id, Name = "Sara Khan", Phone = "03004445566", Address = "Model Town Lahore" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFactory.Dispose();
    }

    private ConversationEngine Engine() => new(_db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
        features: new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false },
        turn: _turn.Object);

    private async Task SayAsync(string message, TurnAction understood)
    {
        _turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), message, It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>())).ReturnsAsync(understood);
        await Engine().HandleIncomingMessageAsync(Phone, message, default);
    }

    /// <summary>The older AI steps (order reader, intent router, planner) were not asked: the one call was the whole answer.</summary>
    private void OlderPipelineWasNotAsked()
    {
        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _ai.Verify(a => a.RouteIntentAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<OrderTrackerBot.Application.Intents.IntentDefinition>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HowTo_question_gets_the_way_to_do_it_and_changes_nothing()
    {
        await SayAsync("میں پراڈکٹ میں کیسے ایڈ کر سکتا؟", new TurnAction { Action = TurnAction.HowTo, Topic = TurnAction.AddProduct });

        var reply = Assert.Single(_sent);
        Assert.Contains("Naya product rakhne ke liye", reply);
        Assert.Equal(1, await _db.Products.CountAsync());
        Assert.Empty(await _db.MerchantFeedbacks.ToListAsync());
        OlderPipelineWasNotAsked();
    }

    [Fact]
    public async Task Question_from_the_model_is_asked_instead_of_guessing()
    {
        await SayAsync("kya aap mujhe contact number bata sakte hain", new TurnAction { Action = TurnAction.Clarify, Question = "Kis customer ka number chahiye?" });

        Assert.Equal("❓ Kis customer ka number chahiye?", Assert.Single(_sent));
        Assert.Empty(await _db.MerchantFeedbacks.ToListAsync());
        OlderPipelineWasNotAsked();
    }

    [Theory]
    [InlineData(TurnAction.HelpOrChat, "dukaan ka hisaab rakhta hoon")]
    [InlineData(TurnAction.OutOfScope, "yeh kaam abhi main nahi karta")]
    [InlineData(TurnAction.CreateOrder, "poori baat samajh nahi aayi")]   // an order with no details: asked again, nothing guessed
    public async Task Nothing_to_run_gets_one_plain_reply(string action, string expected)
    {
        await SayAsync("kuch bhi keh diya", new TurnAction { Action = action });

        Assert.Contains(expected, Assert.Single(_sent));
        OlderPipelineWasNotAsked();
    }

    [Fact]
    public async Task New_customer_is_saved_with_what_was_said()
    {
        await SayAsync("naya gahak hai Bilal Ahmed, Gulberg Lahore, 03001234567",
            new TurnAction { Action = TurnAction.AddCustomer, Customer = new AiNewCustomer { Name = "Bilal Ahmed", Phone = "03001234567", Address = "Gulberg Lahore" } });

        var saved = await _db.Customers.SingleAsync(c => c.Name == "Bilal Ahmed");
        Assert.Equal("03001234567", saved.Phone);
        Assert.Equal("Gulberg Lahore", saved.Address);
        OlderPipelineWasNotAsked();
    }

    [Fact]
    public async Task Phone_the_seller_never_said_is_not_saved()
    {
        await SayAsync("ek banda aata hai Bilal Ahmed usko yaad rakh lo",
            new TurnAction { Action = TurnAction.AddCustomer, Customer = new AiNewCustomer { Name = "Bilal Ahmed", Phone = "03001234567" } });

        Assert.False(await _db.Customers.AnyAsync(c => c.Name == "Bilal Ahmed"));
        Assert.Contains("03001234567", Assert.Single(_sent));
    }

    [Fact]
    public async Task Customer_question_shows_the_saved_number()
    {
        await SayAsync("number bata do Sara ka zara", new TurnAction { Action = TurnAction.ShowCustomer, CustomerName = "Sara" });

        Assert.Contains("03004445566", Assert.Single(_sent));
    }

    [Fact]
    public async Task Price_change_waits_for_yes()
    {
        await SayAsync("kurti ka rate ab 2000 hai",
            new TurnAction { Action = TurnAction.EditProduct, Products = { new AiNewProduct { Name = "Kurti", Price = 2000 } } });

        Assert.Contains("Kurti ki qeemat", Assert.Single(_sent));
        Assert.Equal(1800, (await _db.Products.SingleAsync()).Price);

        await Engine().HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(2000, (await _db.Products.SingleAsync()).Price);
    }

    [Fact]
    public async Task Price_change_is_dropped_on_no()
    {
        await SayAsync("kurti ka rate ab 2000 hai",
            new TurnAction { Action = TurnAction.EditProduct, Products = { new AiNewProduct { Name = "Kurti", Price = 2000 } } });
        await Engine().HandleIncomingMessageAsync(Phone, "no", default);

        Assert.Equal(1800, (await _db.Products.SingleAsync()).Price);
    }

    [Fact]
    public async Task Customer_phone_change_waits_for_yes()
    {
        await SayAsync("Sara ka naya number 03339998877 hai",
            new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Sara", Field = "phone", Value = "03339998877" });

        Assert.Equal("03004445566", (await _db.Customers.SingleAsync()).Phone);
        await Engine().HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal("03339998877", (await _db.Customers.SingleAsync()).Phone);
    }

    [Fact]
    public async Task Status_by_customer_name_asks_about_their_latest_order()
    {
        var customer = await _db.Customers.SingleAsync();
        var order = new OrderTrackerBot.Domain.Entities.Order { SellerId = _seller.Id, CustomerId = customer.Id, Status = OrderStatus.Pending };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        await SayAsync("Sara ka parcel bhej diya hai", new TurnAction { Action = TurnAction.UpdateStatus, CustomerName = "Sara Khan", Status = "shipped" });

        Assert.Contains($"#{order.Id}", Assert.Single(_sent));
        Assert.Equal(OrderStatus.Pending, (await _db.Orders.SingleAsync()).Status);
        await Engine().HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(OrderStatus.Shipped, (await _db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Support_is_reached_only_when_the_model_says_so()
    {
        await SayAsync("mujhe kisi banday se baat karni hai", new TurnAction { Action = TurnAction.ContactSupport });

        Assert.Single(await _db.MerchantFeedbacks.ToListAsync());
    }

    private async Task HearAsync(string transcript, TurnAction understood)
    {
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync("voice-1", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[2500], "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), "audio/ogg", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(transcript);
        _turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), transcript, It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>())).ReturnsAsync(understood);
        var engine = new ConversationEngine(_db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), media: media.Object, transcriber: transcriber.Object,
            features: new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false, VoiceAck = false },
            turn: _turn.Object);
        await engine.HandleAudioMessageAsync(Phone, "voice-1");
    }

    // The live failure: a spoken "how do I add a product" was echoed twice and then ran "contact support".
    [Fact]
    public async Task Spoken_how_to_is_echoed_once_and_answered_without_the_voice_rewrite()
    {
        await HearAsync("میں نے نئی پراڈکٹ ایڈ کرنی ہے وہ کیسے کر سکتا ہوں؟", new TurnAction { Action = TurnAction.HowTo, Topic = TurnAction.AddProduct });

        Assert.Equal(2, _sent.Count);
        Assert.StartsWith("🎤 Maine suna", _sent[0]);
        Assert.Contains("Naya product rakhne ke liye", _sent[1]);
        Assert.Empty(await _db.MerchantFeedbacks.ToListAsync());
        _ai.Verify(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        OlderPipelineWasNotAsked();
    }

    [Fact]
    public async Task Spoken_contact_number_question_is_asked_back_not_sent_to_support()
    {
        await HearAsync("کیا آپ مجھے کانٹیکٹ نمبر بتا سکتے ہیں؟", new TurnAction { Action = TurnAction.Clarify, Question = "Kis customer ka number chahiye?" });

        Assert.Equal(2, _sent.Count);
        Assert.Equal("❓ Kis customer ka number chahiye?", _sent[1]);
        Assert.Empty(await _db.MerchantFeedbacks.ToListAsync());
    }

    [Fact]
    public async Task Spoken_new_product_is_saved()
    {
        await HearAsync("شال پچیس سو روپے کی ہے اسے لسٹ میں شامل کر دو",
            new TurnAction { Action = TurnAction.AddProduct, Products = { new AiNewProduct { Name = "Shawl", Price = 2500 } } });

        Assert.Equal(2500, (await _db.Products.SingleAsync(p => p.Name == "Shawl")).Price);
    }

    [Fact]
    public async Task Exact_typed_command_never_goes_to_the_model()
    {
        await Engine().HandleIncomingMessageAsync(Phone, "orders today", default);

        _turn.Verify(t => t.UnderstandAsync(It.IsAny<TurnContext>(), It.IsAny<string>(), It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Stray_fields_of_another_action_are_dropped()
    {
        var action = OrderTrackerBot.Infrastructure.Ai.OpenAiTurnInterpreter.Parse(
            """{"action":"show_order","order_id":2,"customer_name":null,"orders":[{"customer_name":"Ali Raza","items":[{"product_name":"Lawn Suit","quantity":1,"unit_price":null}]}],"products":[{"name":"Kurti","price":1800}],"customer":{"name":"Ali Raza","phone":"03215557788","address":null,"city":null}}""");

        Assert.NotNull(action);
        Assert.Empty(action!.Orders);
        Assert.Empty(action.Products);
        Assert.Null(action.Customer);
        Assert.Equal(2, action.OrderId);
    }
}
