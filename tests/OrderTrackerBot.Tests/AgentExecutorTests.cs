using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Agent;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Features:Agent: a message nothing else places is planned, verified, compiled to typed messages and run through the normal pipeline.
public class AgentExecutorTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _text = new();

    public AgentExecutorTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _text.Add(body)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine Engine(AppDbContext db, bool agent = true) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false, Agent = agent });

    private static async Task<Seller> SeedAsync(AppDbContext db)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        return seller;
    }

    private void Plans(string message, AgentPlan plan) =>
        _ai.Setup(a => a.PlanAsync(message, It.IsAny<string>(), It.IsAny<IReadOnlyList<AgentTool>>(), It.IsAny<CancellationToken>())).ReturnsAsync(plan);

    private static PlanStep Add(string name, string price) => new("add_product", new Dictionary<string, string> { ["name"] = name, ["price"] = price }, Array.Empty<PlanItem>());

    private static AgentPlan Steps(params PlanStep[] steps) => new(steps, null, null, 0.9);

    private string Said => string.Join("\n---\n", _text);

    [Fact]
    public async Task FlagOff_ThePlannerIsNeverAsked()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db, agent: false).HandleIncomingMessageAsync(Phone, "pehle Hat 100 ka add karo phir Cap 200 ka", default);

        _ai.Verify(a => a.PlanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<AgentTool>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, await db.Products.CountAsync());
    }

    [Fact]
    public async Task TwoNewProducts_AreSavedInOrder_AndTheSellerSeesWhatWasUnderstood()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        const string msg = "pehle Hat 100 ka add karo phir Cap 200 ka";
        Plans(msg, Steps(Add("Hat", "100"), Add("Cap", "200")));

        await Engine(db).HandleIncomingMessageAsync(Phone, msg, default);

        Assert.Equal(new[] { "Hat", "Cap" }, (await db.Products.OrderBy(p => p.Id).Select(p => p.Name).ToListAsync()));
        Assert.Equal(new[] { 100m, 200m }, (await db.Products.OrderBy(p => p.Id).Select(p => p.Price).ToListAsync()));
        Assert.Contains("Samjha", Said);
    }

    [Fact]
    public async Task APriceTheSellerNeverSaid_SavesNothing_AndSaysSo()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        const string msg = "pehle Hat 100 ka add karo phir Cap 200 ka";
        Plans(msg, Steps(Add("Hat", "100"), Add("Cap", "999")));

        await Engine(db).HandleIncomingMessageAsync(Phone, msg, default);

        Assert.Equal(new[] { "Hat" }, await db.Products.Select(p => p.Name).ToListAsync());   // the step before the bad one still ran
        Assert.Contains("samajh nahi aaya", Said);
    }

    [Fact]
    public async Task EditingAnExistingProduct_WaitsForYes_ThenRunsInOrder()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        var polo = new Product { SellerId = seller.Id, Name = "Polo Shirt", Price = 500 };
        db.Products.Add(polo);
        await db.SaveChangesAsync();
        const string msg = "pehle Hat 100 ka add karo phir Polo ka 600 karo";
        Plans(msg, Steps(Add("Hat", "100"),
            new PlanStep("edit_product", new Dictionary<string, string> { ["product_id"] = polo.Id.ToString(), ["price"] = "600" }, Array.Empty<PlanItem>())));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, msg, default);

        Assert.Equal(500m, (await db.Products.AsNoTracking().FirstAsync(p => p.Id == polo.Id)).Price);   // nothing changed yet
        Assert.True(ConversationState.AwaitingVoiceConfirmation == (await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.State, Said);

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(600m, (await db.Products.AsNoTracking().FirstAsync(p => p.Id == polo.Id)).Price);
    }

    [Fact]
    public async Task EditingFirst_ParksTheWholeRemainder_SoOrderIsKept()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        var polo = new Product { SellerId = seller.Id, Name = "Polo Shirt", Price = 500 };
        db.Products.Add(polo);
        await db.SaveChangesAsync();
        const string msg = "pehle Polo ka 600 karo phir Hat 100 ka add karo";
        Plans(msg, Steps(
            new PlanStep("edit_product", new Dictionary<string, string> { ["product_id"] = polo.Id.ToString(), ["price"] = "600" }, Array.Empty<PlanItem>()),
            Add("Hat", "100")));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, msg, default);
        Assert.Equal(0, await db.Products.CountAsync(p => p.Name == "Hat"));   // Hat comes after the edit, so it waits too

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(1, await db.Products.CountAsync(p => p.Name == "Hat"));
        Assert.Equal(600m, (await db.Products.AsNoTracking().FirstAsync(p => p.Id == polo.Id)).Price);
    }

    [Fact]
    public async Task TwoCustomersWithTheName_ArePutAsAQuestion_AndNothingIsDrafted()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara Khan", City = "Lahore" });
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara Ali", City = "Karachi" });
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Shirt", Price = 500 });
        await db.SaveChangesAsync();
        const string msg = "Sara ko 2 Shirt bhej do";
        Plans(msg, Steps(new PlanStep("draft_order", new Dictionary<string, string> { ["customer_name"] = "Sara" }, new[] { new PlanItem(null, "Shirt", 2) })));

        await Engine(db).HandleIncomingMessageAsync(Phone, msg, default);

        Assert.Contains("Sara Khan (Lahore)", Said);
        Assert.Contains("Sara Ali (Karachi)", Said);
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(ConversationState.Idle, (await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.State);
    }

    [Fact]
    public async Task OrderForASavedCustomer_BecomesADraftWaitingForSave()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara Khan", Phone = "03001234567", City = "Lahore" });
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Shirt", Price = 500 });
        await db.SaveChangesAsync();
        const string msg = "Sara ko 2 Shirt bhej do";
        Plans(msg, Steps(new PlanStep("draft_order", new Dictionary<string, string> { ["customer_name"] = "Sara" }, new[] { new PlanItem(null, "Shirt", 2) })));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, msg, default);
        Assert.Equal(0, await db.Orders.CountAsync());   // a draft is not an order yet
        Assert.Contains("Sara Khan", Said);

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.Include(o => o.Items).SingleAsync();
        Assert.Equal(2, order.Items.Single().Quantity);
        Assert.Equal(1000m, order.Subtotal);
    }

    // ---- "I will send them one by one" ----

    private static AgentPlan Promise(int count) =>
        new(Array.Empty<PlanStep>(), "Pehla product likhein: Name - price", new PlanAwaiting("add_product", count), 0.9);

    [Fact]
    public async Task APromisedCount_IsCounted_AndEndsWithASummary()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        const string msg = "do cheezein ek ek karke bhejunga";
        Plans(msg, Promise(2));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, msg, default);
        Assert.Contains("Pehla product likhein", Said);

        await engine.HandleIncomingMessageAsync(Phone, "Shirt - 500", default);
        await engine.HandleIncomingMessageAsync(Phone, "Dupatta - 300", default);

        Assert.Equal(2, await db.Products.CountAsync());
        Assert.Contains("2 products save ho gaye", Said);
        Assert.Null(SessionContextData.FromJson((await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.ContextJson).AgentAwaiting);

        _text.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "Shawl - 900", default);   // after the count, a product line is just a product line
        Assert.Equal(3, await db.Products.CountAsync());
        Assert.DoesNotContain("save ho gaye", Said);
    }

    [Fact]
    public async Task APromiseWithoutACount_EndsOnDone()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        const string msg = "kuch cheezein ek ek karke bhejunga";
        Plans(msg, Promise(0));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, msg, default);
        await engine.HandleIncomingMessageAsync(Phone, "Shirt - 500", default);
        await engine.HandleIncomingMessageAsync(Phone, "Dupatta - 300", default);
        await engine.HandleIncomingMessageAsync(Phone, "Shawl - 900", default);
        Assert.True(SessionContextData.FromJson((await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.ContextJson).AgentAwaiting?.Done == 3, Said);

        _text.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "done", default);

        Assert.Contains("3 products save hue", Said);
        Assert.Null(SessionContextData.FromJson((await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.ContextJson).AgentAwaiting);
    }

    [Fact]
    public async Task ADifferentRealCommand_LeavesThePromise_AndRuns()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        const string msg = "teen cheezein ek ek karke bhejunga";
        Plans(msg, Promise(3));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, msg, default);
        await engine.HandleIncomingMessageAsync(Phone, "Shirt - 500", default);
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.Null(SessionContextData.FromJson((await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.ContextJson).AgentAwaiting);
        Assert.Equal(1, await db.Products.CountAsync());
    }

    [Fact]
    public async Task AStepTogetherWithAnAwaitingPromise_IsJustTheStep()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        const string msg = "pehle Shawl 3000 ka add karo phir kuch aur";
        Plans(msg, new AgentPlan(new[] { Add("Shawl", "3000") }, "Shawl ka price kya hai?", new PlanAwaiting("add_product", 1), 0.9));

        await Engine(db).HandleIncomingMessageAsync(Phone, msg, default);

        Assert.Equal(1, await db.Products.CountAsync());
        Assert.DoesNotContain("price kya hai", Said);
        Assert.Null(SessionContextData.FromJson((await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.ContextJson).AgentAwaiting);
    }

    [Fact]
    public async Task AQuestion_KeepsTheRequest_AndTheAnswerCompletesIt()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        // "Shawl add karo" has no price: the planner asks, and the request is kept
        Plans("Shawl add karo", Steps(new PlanStep("add_product", new Dictionary<string, string> { ["name"] = "Shawl" }, Array.Empty<PlanItem>())));
        // the seller answers with the price alone; it is read together with the request
        Plans("Shawl add karo. 3000", Steps(Add("Shawl", "3000")));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "Shawl add karo", default);
        var waiting = SessionContextData.FromJson((await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.ContextJson);
        Assert.Equal("Shawl add karo", waiting.AgentPending!.Message);
        Assert.Equal(0, await db.Products.CountAsync(p => p.Name == "Shawl"));

        await engine.HandleIncomingMessageAsync(Phone, "3000", default);

        Assert.Equal(3000m, (await db.Products.AsNoTracking().FirstAsync(p => p.Name == "Shawl")).Price);
        Assert.Null(SessionContextData.FromJson((await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.ContextJson).AgentPending);
    }

    [Fact]
    public async Task AQuestion_IsDroppedAfterTheWindow_AndARealCommandLeavesIt()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        var session = seller.Session!;
        session.ContextJson = new SessionContextData { AgentPending = new AgentPendingData { Message = "Shawl add karo", Question = "q", At = DateTime.UtcNow.AddMinutes(-30) } }.ToJson();
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, "3000", default);

        Assert.Equal(0, await db.Products.CountAsync(p => p.Name == "Shawl"));   // too old: "3000" is not read as the price of an old request
        Assert.Null(SessionContextData.FromJson((await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.ContextJson).AgentPending);
    }

    [Fact]
    public async Task AQuestionForNothingTheToolsCanDo_IsNotAskedAndNothingRuns()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        const string msg = "Ali ne paisay bhej diye";
        Plans(msg, new AgentPlan(Array.Empty<PlanStep>(), "Ali ka phone number kya hai?", null, 0.9, "none"));

        await Engine(db).HandleIncomingMessageAsync(Phone, msg, default);

        Assert.DoesNotContain("phone number kya hai", Said);
        Assert.Null(SessionContextData.FromJson((await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.ContextJson).AgentPending);
        Assert.Equal(0, await db.Products.CountAsync());
    }

    [Fact]
    public async Task ATwoSaras_AskWhich_AndTheAnswerCompletesTheOrder()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara Khan", Phone = "03001112222", City = "Lahore" });
        var ali = new Customer { SellerId = seller.Id, Name = "Sara Ali", Phone = "03003334444", City = "Karachi" };
        db.Customers.Add(ali);
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Shirt", Price = 1800 });
        await db.SaveChangesAsync();
        const string msg = "Sara ko 2 Shirt bhej do";
        Plans(msg, Steps(new PlanStep("draft_order", new Dictionary<string, string> { ["customer_name"] = "Sara" }, new[] { new PlanItem(null, "Shirt", 2) })));
        // the seller's answer is read with the request: the planner now sees the full name
        Plans($"{msg}. Sara Ali", Steps(new PlanStep("draft_order", new Dictionary<string, string> { ["customer_id"] = ali.Id.ToString() }, new[] { new PlanItem(null, "Shirt", 2) })));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, msg, default);
        Assert.Contains("Sara Ali (Karachi)", Said);
        Assert.Equal(0, await db.Orders.CountAsync());

        await engine.HandleIncomingMessageAsync(Phone, "Sara Ali", default);
        Assert.Contains("Sara Ali", Said);
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.State);
    }

    [Fact]
    public async Task AProductWithVariants_AsksWhich_AndTheAnswerCompletesTheOrder()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db);
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" });
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Shirt white", Price = 1800 });
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Shirt black", Price = 1800 });
        await db.SaveChangesAsync();
        const string msg = "Sara ko Shirt dedo";
        Plans(msg, Steps(new PlanStep("draft_order", new Dictionary<string, string> { ["customer_name"] = "Sara" }, new[] { new PlanItem(null, "Shirt", 1) })));
        Plans($"{msg}. white", Steps(new PlanStep("draft_order", new Dictionary<string, string> { ["customer_name"] = "Sara" }, new[] { new PlanItem(null, "Shirt white", 1) })));
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, msg, default);
        Assert.Contains("Shirt white", Said);
        Assert.Equal(0, await db.Orders.CountAsync());

        await engine.HandleIncomingMessageAsync(Phone, "white", default);
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sellers.Include(s => s.Session).AsNoTracking().FirstAsync()).Session!.State);
    }

    [Fact]
    public async Task APlanBelowTheConfidenceBar_IsIgnored()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        const string msg = "pehle Hat 100 ka add karo phir Cap 200 ka";
        Plans(msg, new AgentPlan(new[] { Add("Hat", "100") }, null, null, 0.3));

        await Engine(db).HandleIncomingMessageAsync(Phone, msg, default);

        Assert.Equal(0, await db.Products.CountAsync());
    }
}
