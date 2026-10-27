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

        Assert.True(await db.Customers.AnyAsync(c => c.Name == "Muhammad Ali" && c.City == "Lahore"));
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
