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
}
