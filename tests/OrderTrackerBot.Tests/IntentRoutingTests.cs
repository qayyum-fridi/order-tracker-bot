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

// A message the order reader cannot place goes to the intent catalog: the model picks the action, the engine builds and runs the command.
public class IntentRoutingTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _text = new();

    public IntentRoutingTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        // A question with tap buttons (reset: Yes / No) is still the question the seller reads, so its body counts as shown text.
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _text.Add(body)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private ConversationEngine Engine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false });

    private static async Task SeedAsync(AppDbContext db)
    {
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RoutedExpense_IsSaved_FromTheSlotsTheModelFilled()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync("aaj 500 ka petrol kharcha hua", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("expense.add", new Dictionary<string, string> { ["amount"] = "500", ["note"] = "petrol" }, 0.9));

        await Engine(db).HandleIncomingMessageAsync(Phone, "aaj 500 ka petrol kharcha hua", default);

        Assert.Equal(1, await db.Expenses.CountAsync());
    }

    [Fact]
    public async Task WithTheFlagOn_TheRouterIsToldAboutTheSeller_AndTheRoutedActionStillRuns()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var seller = await db.Sellers.FirstAsync();
        seller.BusinessType = "Grocery / Kiryana";
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Basmati Rice", Price = 300, UnitType = "kg", UnitQty = 1 });
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Sugar", Price = 150, UnitType = "kg", UnitQty = 1 });
        await db.SaveChangesAsync();
        AiRouteContext? told = null;
        _ai.Setup(a => a.RouteIntentWithContextAsync("aaj 500 ka petrol kharcha hua", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<AiRouteContext>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<IntentDefinition>, AiRouteContext, CancellationToken>((_, _, c, _) => told = c)
            .ReturnsAsync(new IntentRoute("expense.add", new Dictionary<string, string> { ["amount"] = "500", ["note"] = "petrol" }, 0.9));
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false, RouterContext = true });

        await engine.HandleIncomingMessageAsync(Phone, "aaj 500 ka petrol kharcha hua", default);

        Assert.Equal(1, await db.Expenses.CountAsync());
        Assert.NotNull(told);
        Assert.Equal("Grocery / Kiryana", told!.ShopType);
        Assert.Contains("Sugar", told.ProductNames);
        Assert.Contains("Basmati Rice", told.ProductNames);
        Assert.False(string.IsNullOrWhiteSpace(told.Situation));
    }

    [Fact]
    public async Task WithTheFlagOff_TheRouterGetsOnlyTheMessage_AsBefore()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync("aaj 500 ka petrol kharcha hua", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("expense.add", new Dictionary<string, string> { ["amount"] = "500", ["note"] = "petrol" }, 0.9));

        await Engine(db).HandleIncomingMessageAsync(Phone, "aaj 500 ka petrol kharcha hua", default);

        Assert.Equal(1, await db.Expenses.CountAsync());
        _ai.Verify(a => a.RouteIntentWithContextAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<AiRouteContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RoutedCustomerWithoutName_GetsItsHowTo_AndSavesNothing()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync("naya client hai", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("customer.add", new Dictionary<string, string>(), 0.9));

        await Engine(db).HandleIncomingMessageAsync(Phone, "naya client hai", default);

        var saved = await db.Customers.Select(c => c.Name).ToListAsync();
        Assert.True(saved.Count == 0, "saved: " + string.Join(" | ", saved) + " ; replies: " + string.Join(" || ", _text));
        Assert.Contains(_text, t => t.Contains("customer add [name]"));
    }

    [Fact]
    public async Task RoutedResetAccount_OnlyStartsTheConfirmation_AndDeletesNothing()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync("sab data mita do", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("account.reset", new Dictionary<string, string>(), 0.95));

        await Engine(db).HandleIncomingMessageAsync(Phone, "sab data mita do", default);

        Assert.Contains(_text, t => t.Contains("poora data"));
        Assert.Equal(1, await db.Sellers.CountAsync());
    }

    [Fact]
    public async Task LowConfidenceRoute_RunsNothing()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync("500 petrol", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("expense.add", new Dictionary<string, string> { ["amount"] = "500", ["note"] = "petrol" }, 0.4));

        await Engine(db).HandleIncomingMessageAsync(Phone, "500 petrol", default);

        Assert.Equal(0, await db.Expenses.CountAsync());
    }

    [Fact]
    public async Task InventedIntent_RunsNothing()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        _ai.Setup(a => a.RouteIntentAsync("sab kuch mita do", It.IsAny<IReadOnlyList<IntentDefinition>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentRoute("data.delete_all", new Dictionary<string, string>(), 0.99));

        await Engine(db).HandleIncomingMessageAsync(Phone, "sab kuch mita do", default);

        Assert.Equal(0, await db.Expenses.CountAsync());
    }

    public void Dispose() => _dbFactory.Dispose();
}
