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

// A new seller's first steps with no AI reachable: found by driving a fresh seller through the webhook.
public class FirstRunTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();
    private readonly List<IReadOnlyList<MenuSection>> _lists = new();

    public FirstRunTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, sections, _) => { _sent.Add(body); _lists.Add(sections); }).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine Engine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false, SuggestionButtons = true, ProductConfirmation = true });

    private async Task<(AppDbContext Db, ConversationEngine Engine)> SetUpSellerAsync(SessionContextData? ctx = null)
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller
        {
            WhatsAppPhoneNumber = Phone, BusinessName = "Ali Store", OnboardingComplete = true, SalesMode = ctx?.AwaitingSalesMode == true ? null : "delivery",
            Session = new ConversationSession { State = ConversationState.Idle, ContextJson = (ctx ?? new SessionContextData()).ToJson() }
        };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        return (db, Engine(db));
    }

    [Fact]
    public async Task ShopNameTypedAtTheStartChoice_IsSaved_NotAskedForAgain()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        Assert.Equal(ConversationState.OnboardingStartChoice, (await db.Sessions.SingleAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "Ali Clothing", default);

        Assert.Equal("Ali Clothing", (await db.Sellers.SingleAsync()).BusinessName);
        Assert.NotEqual(ConversationState.OnboardingBusinessName, (await db.Sessions.SingleAsync()).State);
    }

    [Fact]
    public async Task TypedNumberAtTheStartChoice_PicksThatButton()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);

        await engine.HandleIncomingMessageAsync(Phone, "1", default);

        Assert.Equal(ConversationState.OnboardingBusinessName, (await db.Sessions.SingleAsync()).State);
        Assert.Null((await db.Sellers.SingleAsync()).BusinessName);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("...")]
    public async Task NumberAtTheShopNameQuestion_IsNotSavedAsTheName(string typed)
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, typed, default);

        Assert.Null((await db.Sellers.SingleAsync()).BusinessName);
        Assert.Equal(ConversationState.OnboardingBusinessName, (await db.Sessions.SingleAsync()).State);
        Assert.Contains(MessageTemplates.Get("business.name.invalid", typed), _sent);
    }

    [Fact]
    public async Task OrderItemWrittenWithItsPackSize_MatchesTheCatalogProduct()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await engine.HandleIncomingMessageAsync(Phone, "Chini 5 kg - 950", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var chini = await db.Products.SingleAsync();
        Assert.Equal(("Chini", "kg", 5m), (chini.Name, chini.UnitType, chini.UnitQty));
        await engine.HandleIncomingMessageAsync(Phone, "customer add Bilal, Model Town, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "order for Bilal", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Chini 5 kg 2", default);

        Assert.DoesNotContain(_sent, m => m.Contains("catalog mein nahi mila"));
        Assert.Contains(_sent, m => m.Contains("Rs.1,900"));
    }

    [Fact]
    public async Task ProductWaitingForYes_DroppedByAnotherCommand_TellsTheSeller()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await engine.HandleIncomingMessageAsync(Phone, "Chawal 5 kg - 1200, stock 10", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.Contains(MessageTemplates.Get("product.pending.dropped", "Chawal"), _sent);
        Assert.Empty(await db.Products.ToListAsync());
    }

    [Fact]
    public async Task ProductWaitingForYes_ReplacedByANewProductLine_ShowsNoDroppedNotice()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await engine.HandleIncomingMessageAsync(Phone, "Chawal 5 kg - 1200", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Chawal 5 kg - 1250", default);

        Assert.DoesNotContain(_sent, m => m.Contains("save nahi hua"));
    }

    [Theory]
    [InlineData("customer add Bilal 03001234567", "Bilal", "03001234567", null)]
    [InlineData("customer add Bilal Ahmed 0300 1234567", "Bilal Ahmed", "0300 1234567", null)]
    [InlineData("add customer Sara 03211234567 Model Town", "Sara", "03211234567", "Model Town")]
    public void CustomerAdd_WithoutCommas_SplitsNamePhoneAndAddress(string text, string name, string phone, string? address)
    {
        var cmd = CommandParser.TryParse(text);

        Assert.NotNull(cmd);
        Assert.Equal(CommandKind.CustomerAdd, cmd!.Kind);
        Assert.Equal((name, phone, address), (cmd.Text, cmd.Text2, cmd.Text3));
    }

    private async Task SellTwoChiniAsync(ConversationEngine engine, string productLine)
    {
        await engine.HandleIncomingMessageAsync(Phone, productLine, default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        await engine.HandleIncomingMessageAsync(Phone, "customer add Bilal, Model Town, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "order for Bilal", default);
        await engine.HandleIncomingMessageAsync(Phone, "Chini 5 kg 2", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
    }

    [Fact]
    public async Task DayClose_ExpensesAndWagesAboveProfit_SaysItWasALoss()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950, cost 880, stock 40");
        Assert.Single(await db.Orders.ToListAsync());
        await engine.HandleIncomingMessageAsync(Phone, "bijli ka bill 8000", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ali ko 1500 dihari di", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "aaj ka hisaab", default);

        // Profit 2 x (950 - 880) = 140; 140 - 8,000 - 1,500 = -9,360.
        var close = Assert.Single(_sent, m => m.Contains("Reconciliation"));
        Assert.Contains(MessageTemplates.Get("day-close.wages", "Rs.1,500"), close);
        Assert.Contains(MessageTemplates.Get("day-close.net", "-Rs.9,360", "Rs.140", "Rs.8,000", "Rs.1,500"), close);
        Assert.EndsWith(MessageTemplates.Get("day-close.loss", "Rs.9,360"), close);
        Assert.DoesNotContain(MessageTemplates.Get("day-close.ok"), close);
    }

    [Fact]
    public async Task DayClose_ProfitAboveExpenses_SaysAllFine()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950, cost 880, stock 40");
        await engine.HandleIncomingMessageAsync(Phone, "chai pe 100 lage", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "aaj ka hisaab", default);

        var close = Assert.Single(_sent, m => m.Contains("Reconciliation"));
        Assert.Contains("📊", close);
        Assert.EndsWith(MessageTemplates.Get("day-close.ok"), close);
    }

    [Fact]
    public async Task DayClose_ProductWithoutCostPrice_SaysTheResultCannotBeWorkedOut()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950");
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "aaj ka hisaab", default);

        var close = Assert.Single(_sent, m => m.Contains("Reconciliation"));
        Assert.EndsWith(MessageTemplates.Get("day-close.nocost", 1), close);
        Assert.DoesNotContain("📊", close);
    }

    private IReadOnlyList<MenuRow> LastListRows() => _lists.Last().SelectMany(s => s.Rows).ToList();

    private async Task AddProductAsync(ConversationEngine engine, string line)
    {
        await engine.HandleIncomingMessageAsync(Phone, line, default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
    }

    [Theory]
    [InlineData("Kurta White - 2200, cost 1400, stock 10, size M", 2200)]
    [InlineData("Lawn Suit 3 Piece - 4500, cost 3000, stock 15", 4500)]
    public async Task NewOrder_ByTapsOnly_NewCustomerThenProduct_IsSaved(string productLine, decimal total)
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await AddProductAsync(engine, productLine);

        await engine.HandleIncomingMessageAsync(Phone, "Naya order", default);
        var newCustomerRow = Assert.Single(LastListRows(), r => r.Id == ConversationEngine.NewCustomerForOrderRowId);
        await engine.HandleIncomingMessageAsync(Phone, newCustomerRow.Id, default);
        Assert.Contains(MessageTemplates.Get("tapflow.customer.ask"), _sent);

        await engine.HandleIncomingMessageAsync(Phone, "Hina 03331234567 Johar Town", default);
        var hina = await db.Customers.SingleAsync();
        Assert.Equal(("Hina", "03331234567"), (hina.Name, hina.Phone));
        var productRow = Assert.Single(LastListRows());

        await engine.HandleIncomingMessageAsync(Phone, productRow.Id, default);
        await engine.HandleIncomingMessageAsync(Phone, "✅ Save", default);

        var order = await db.Orders.Include(o => o.Items).SingleAsync();
        Assert.Equal(total, order.Total);
        Assert.Equal(1, Assert.Single(order.Items).Quantity);
    }

    [Fact]
    public async Task NewOrder_Picker_ListsSavedCustomers_AndATapStartsTheirOrder()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await AddProductAsync(engine, "Kurta - 2200");
        await engine.HandleIncomingMessageAsync(Phone, "customer add Sara, Gulberg, 03001234567", default);

        await engine.HandleIncomingMessageAsync(Phone, "new order", default);
        var sara = Assert.Single(LastListRows(), r => r.Title.Contains("Sara"));
        await engine.HandleIncomingMessageAsync(Phone, sara.Id, default);
        await engine.HandleIncomingMessageAsync(Phone, Assert.Single(LastListRows()).Id, default);
        await engine.HandleIncomingMessageAsync(Phone, "✅ Save", default);

        Assert.Equal("Sara", (await db.Orders.Include(o => o.Customer).SingleAsync()).Customer!.Name);
    }

    [Fact]
    public async Task KharchaTapped_ThenPlainWords_SavesTheExpense()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "expenses", default);
        await engine.HandleIncomingMessageAsync(Phone, "dukaan ka kiraya 3,000", default);

        var expense = await db.Expenses.SingleAsync();
        Assert.Equal(3000m, expense.Amount);
    }

    [Fact]
    public async Task TankhwahTapped_ThenNameAndAmount_SavesTheWage()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "wages", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ali 1500", default);

        var wage = await db.WageEntries.SingleAsync();
        Assert.Equal(1500m, wage.Amount);
    }

    [Fact]
    public async Task AddProductTapped_ThenNameAndPriceWithoutDash_ShowsTheProduct()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "add product", default);
        await engine.HandleIncomingMessageAsync(Phone, "lawn suit 4500", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var product = await db.Products.SingleAsync();
        Assert.Equal(4500m, product.Price);
    }

    [Fact]
    public async Task TapJob_IsOnlyTheNextMessage()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "expenses", default);
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);
        await engine.HandleIncomingMessageAsync(Phone, "kiraya 3000 baad mein", default);

        Assert.Empty(await db.Expenses.ToListAsync());
    }

    [Fact]
    public async Task UnpaidList_EachOrderIsATap_ThatMarksItPaid()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950");

        await engine.HandleIncomingMessageAsync(Phone, "unpaid orders", default);
        var row = Assert.Single(LastListRows());
        Assert.Equal("mark 1 paid", row.Id);
        Assert.True(row.Title.Length <= 24, row.Title);
        await engine.HandleIncomingMessageAsync(Phone, row.Id, default);

        Assert.Equal(PaymentStatus.Paid, (await db.Orders.SingleAsync()).PaymentStatus);
    }

    [Fact]
    public void DailyJobs_OfferCollectingMoney()
    {
        Assert.Contains(ConversationEngine.GuideChoices, c => c.Value == "unpaid orders");
    }

    [Theory]
    [InlineData("1", "counter")]
    [InlineData("2", "delivery")]
    public async Task SalesModeQuestion_AnsweredWithATypedNumber_IsSaved(string typed, string expected)
    {
        var (db, engine) = await SetUpSellerAsync(new SessionContextData { AwaitingSalesMode = true, SalesModeJustAsked = true });
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, typed, default);

        Assert.Equal(expected, (await db.Sellers.SingleAsync()).SalesMode);
        Assert.False(SessionContextData.FromJson((await db.Sessions.SingleAsync()).ContextJson).AwaitingSalesMode);
    }
}
