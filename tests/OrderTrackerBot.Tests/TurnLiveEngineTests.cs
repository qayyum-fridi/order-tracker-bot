using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Ai;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// The whole bot on the REAL model, one shop day in free-form sentences (no typed commands): a product, a customer, an order, its status, a question
/// and a how-to. Asserts what ends up in the database and what the seller is told. Only the one understanding call is real; the older AI steps are
/// a mock that must never be needed. Skipped unless OTB_TURN_LIVE=1 and OPENAI_API_KEY are set:
/// <c>OTB_TURN_LIVE=1 OPENAI_API_KEY=... dotnet test --filter TurnLiveEngineTests --logger "console;verbosity=detailed"</c>
/// </summary>
public class TurnLiveEngineTests
{
    private const string Phone = "923001234567";
    private readonly ITestOutputHelper _output;
    public TurnLiveEngineTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A_shop_day_in_plain_sentences()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_TURN_LIVE") != "1" || string.IsNullOrWhiteSpace(key)) return;

        using var factory = new TestDbContextFactory();
        var db = factory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Shirt", Price = 1800 });
        await db.SaveChangesAsync();

        var sent = new List<string>();
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => sent.Add(b)).Returns(Task.CompletedTask);
        var older = new Mock<IAiOrderAssistant>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var interpreter = new OpenAiTurnInterpreter(http, Options.Create(new OpenAiOptions { ApiKey = key }), NullLogger<OpenAiTurnInterpreter>.Instance);

        async Task<string> SayAsync(string message)
        {
            sent.Clear();
            var engine = new ConversationEngine(db, older.Object, sender.Object, Mock.Of<IFounderAlertNotifier>(),
                features: new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false },
                turn: interpreter);
            await engine.HandleIncomingMessageAsync(Phone, message, default);
            var reply = string.Join("\n---\n", sent);
            _output.WriteLine($"> {message}\n{reply}\n");
            return reply;
        }

        // a product, said the way a voice note is transcribed
        await SayAsync("شال پچیس سو روپے کی ہے اسے لسٹ میں شامل کر دو");
        Assert.True(await db.Products.AnyAsync(p => p.Price == 2500), "the 2500 product was not saved");

        // a customer, words in a loose order
        await SayAsync("ek banda hai Bilal Ahmed, Gulberg Lahore mein rehta hai, uska number 03001234567 hai, isko yaad rakh lo");
        var bilal = await db.Customers.SingleOrDefaultAsync(c => c.Name.Contains("Bilal"));
        Assert.NotNull(bilal);
        Assert.Equal("03001234567", bilal!.Phone);

        // an order for that customer, confirmed
        var draft = await SayAsync("do shirt bhejni hain Bilal ko");
        Assert.Contains("Shirt", draft);
        await SayAsync("yes");
        var order = await db.Orders.Include(o => o.Items).SingleOrDefaultAsync();
        Assert.NotNull(order);
        Assert.Equal(2, order!.Items.Single().Quantity);
        Assert.Equal(bilal.Id, order.CustomerId);

        // its status, by the customer's name; nothing changes before YES
        await SayAsync("Bilal ka parcel courier ko de diya hai");
        Assert.Equal(OrderStatus.Pending, (await db.Orders.SingleAsync()).Status);
        await SayAsync("yes");
        Assert.Equal(OrderStatus.Shipped, (await db.Orders.SingleAsync()).Status);

        // a question about a customer is answered from the records
        Assert.Contains("03001234567", await SayAsync("Bilal ka number kya hai zara batao"));

        // a how-to is explained, never run, never a support request
        var products = await db.Products.CountAsync();
        Assert.Contains("Naya product", await SayAsync("میں نے نئی پراڈکٹ ایڈ کرنی ہے وہ کیسے کر سکتا ہوں؟"));
        Assert.Equal(products, await db.Products.CountAsync());
        Assert.Empty(await db.MerchantFeedbacks.ToListAsync());

        // the older AI steps were never needed
        older.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        older.Verify(a => a.RouteIntentAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<OrderTrackerBot.Application.Intents.IntentDefinition>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>The seller's own example: mustard oil that comes in a carton of 10, bought by the carton, sold by the piece or the carton.</summary>
    [Fact]
    public async Task A_product_that_comes_in_a_carton()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_TURN_LIVE") != "1" || string.IsNullOrWhiteSpace(key)) return;

        using var factory = new TestDbContextFactory();
        var db = factory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Bismillah Kiryana", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        db.Customers.Add(new Customer { Seller = seller, Name = "Ali Raza", Phone = "03215557788", Address = "Saddar Karachi" });
        await db.SaveChangesAsync();

        var sent = new List<string>();
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => sent.Add(b)).Returns(Task.CompletedTask);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var interpreter = new OpenAiTurnInterpreter(http, Options.Create(new OpenAiOptions { ApiKey = key }), NullLogger<OpenAiTurnInterpreter>.Instance);

        async Task<string> SayAsync(string message)
        {
            sent.Clear();
            var engine = new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(), sender.Object, Mock.Of<IFounderAlertNotifier>(),
                features: new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false },
                turn: interpreter);
            await engine.HandleIncomingMessageAsync(Phone, message, default);
            var reply = string.Join("\n---\n", sent);
            _output.WriteLine($"> {message}\n{reply}\n");
            return reply;
        }

        await SayAsync("mere paas sarson ke tel ki product hai jo carton mein aati hai, fi carton 1000 rupay ka hai aur us mein 10 pieces aate hain, " +
                       "mere paas 3 carton hain, fi piece 130 se 140 mein bechna hai aur carton 1150 se 1200 mein");
        var oil = await db.Products.SingleOrDefaultAsync();
        Assert.NotNull(oil);
        Assert.Equal(140, oil!.Price);
        Assert.Equal(100, oil.CostPrice);
        Assert.Equal(30, oil.StockQty);
        Assert.Equal(new ProductPack("carton", 10, 1200), ProductPack.Of(oil));

        // two cartons: 20 pieces at the carton rate, taken from stock after YES
        Assert.Contains("2,400", await SayAsync("Ali Raza ko 2 carton tel bhej do"));
        await SayAsync("yes");
        var item = (await db.Orders.Include(o => o.Items).SingleAsync()).Items.Single();
        Assert.Equal(20, item.Quantity);
        Assert.Equal(120, item.UnitPrice);
        Assert.Equal(10, (await db.Products.SingleAsync()).StockQty);

        // loose pieces at the piece price
        // a second order for the same customer within minutes: the bot asks whether it is a repeat; "1" = it is a new one
        var loose = await SayAsync("Ali Raza ko 3 tel de do");
        if (!loose.Contains("420")) loose = await SayAsync("1");
        if (loose.Contains("420") && !loose.Contains("saved")) await SayAsync("yes");
        Assert.Equal(7, (await db.Products.SingleAsync()).StockQty);

        // new cartons arrive
        await SayAsync("tel ke 2 carton aur aa gaye hain");
        Assert.Equal(27, (await db.Products.SingleAsync()).StockQty);
    }
}
