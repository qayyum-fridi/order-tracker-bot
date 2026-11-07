using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Ai;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// The whole bot on the real model: the seeded shop, the real order reader, intent catalog and planner, real replies, and the database as the result. Local only
/// (needs a key): <c>OTB_AGENT_LIVE=1 OPENAI_API_KEY=... dotnet test --filter AgentLiveEngineTests --logger "console;verbosity=detailed"</c>.
/// Nothing here is a fixed answer: the test prints what the bot did and asserts only the end state a seller would care about.
/// </summary>
public class AgentLiveEngineTests(ITestOutputHelper output)
{
    private const string Phone = "923001234567";

    private static async Task<Seller> SeedAsync(AppDbContext db)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", BusinessType = "Clothing", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Polo Shirt", Price = 500 });
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Kurti", Price = 1800 });
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Bilal", Phone = "03005556666", City = "Multan" });
        await db.SaveChangesAsync();
        return seller;
    }

    private async Task<string> SendAsync(ConversationEngine engine, AppDbContext db, Mock<IWhatsAppSender> sender, List<string> said, string message)
    {
        said.Clear();
        await engine.HandleIncomingMessageAsync(Phone, message, default);
        var session = (await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!;
        var reply = string.Join("\n---\n", said);
        output.WriteLine($"> {message}\n{reply}\n[state: {session.State}]\n");
        return reply;
    }

    [LocalOnlyFact("OTB_AGENT_LIVE")]
    public async Task Live_TheThreeTaskMessage_AddsEditsAndDrafts_ThenYesFinishesIt()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "OPENAI_API_KEY is needed.");
        using var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        await SeedAsync(db);
        var said = new List<string>();
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<string, string, CancellationToken>((_, t, _) => said.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => said.Add(b)).Returns(Task.CompletedTask);
        var ai = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(90) }, Options.Create(new OpenAiOptions { ApiKey = key }),
            NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());
        var engine = new ConversationEngine(db, ai, sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false, Agent = true });

        await SendAsync(engine, db, sender, said, "Hat 100 ka add karo, Polo Shirt ka price 600 kar do, phir Bilal ko 2 Hat bhej do");
        var hat = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Name == "Hat");
        var polo = await db.Products.AsNoTracking().FirstAsync(p => p.Name == "Polo Shirt");
        Assert.NotNull(hat);                 // the add ran at once
        Assert.Equal(500m, polo.Price);      // the edit waits for YES
        Assert.Equal(0, await db.Orders.CountAsync());

        await SendAsync(engine, db, sender, said, "haan");
        polo = await db.Products.AsNoTracking().FirstAsync(p => p.Name == "Polo Shirt");
        Assert.Equal(600m, polo.Price);      // the YES ran the edit
        var session = (await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!;
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, session.State);   // the order is a draft; the seller still says Save
        Assert.Equal(0, await db.Orders.CountAsync());

        await SendAsync(engine, db, sender, said, "yes");   // the seller confirms the order draft
        Assert.Equal(1, await db.Orders.CountAsync());   // the seller's Save makes the order
    }

    [LocalOnlyFact("OTB_AGENT_LIVE")]
    public async Task Live_AQuestionFromThePlanner_IsAnsweredAndTheRequestCompletes()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "OPENAI_API_KEY is needed.");
        using var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        await SeedAsync(db);
        var said = new List<string>();
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<string, string, CancellationToken>((_, t, _) => said.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => said.Add(b)).Returns(Task.CompletedTask);
        var ai = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(90) }, Options.Create(new OpenAiOptions { ApiKey = key }),
            NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());
        var engine = new ConversationEngine(db, ai, sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false, Agent = true });

        await SendAsync(engine, db, sender, said, "Shawl add karo");
        var reply = await SendAsync(engine, db, sender, said, "3000");

        var shawl = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Name == "Shawl");
        Assert.NotNull(shawl);
        Assert.Equal(3000m, shawl!.Price);
    }

    [LocalOnlyFact("OTB_AGENT_LIVE")]
    public async Task Live_TheSentenceThePlannerWritesForAnOrderWithMoney_IsAcceptedWithoutAskingForAPhone()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(key), "OPENAI_API_KEY is needed.");
        using var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        await SeedAsync(db);
        var said = new List<string>();
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<string, string, CancellationToken>((_, t, _) => said.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => said.Add(b)).Returns(Task.CompletedTask);
        var ai = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(90) }, Options.Create(new OpenAiOptions { ApiKey = key }),
            NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());
        var engine = new ConversationEngine(db, ai, sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false, Agent = true });

        var reply = await SendAsync(engine, db, sender, said, "Bilal ko 2 Kurti, phone 03005556666, delivery 200, advance 500, address Model Town");

        Assert.DoesNotContain("phone number", reply);
        Assert.Contains("200", reply);
        Assert.Contains("500", reply);
    }
}
