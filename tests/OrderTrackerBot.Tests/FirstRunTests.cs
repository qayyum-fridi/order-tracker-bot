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

    [Fact]
    public async Task CustomerPaid_InPlainWords_ListsHerUnpaidOrders_AndATapMarksItPaid()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950");

        await engine.HandleIncomingMessageAsync(Phone, "Bilal ka paisa aa gaya", default);
        Assert.Equal(PaymentStatus.Unpaid, (await db.Orders.SingleAsync()).PaymentStatus); // the sentence alone marks nothing
        Assert.Contains(MessageTemplates.Get("tapflow.paid.owes", "Bilal", 1, "Rs.1,900"), _sent);
        await engine.HandleIncomingMessageAsync(Phone, Assert.Single(LastListRows()).Id, default);

        Assert.Equal(PaymentStatus.Paid, (await db.Orders.SingleAsync()).PaymentStatus);
    }

    [Theory]
    [InlineData("Sara ka paisa aa gaya", "tapflow.paid.nocustomer")]
    [InlineData("Bilal se payment mil gayi", "tapflow.paid.nothing")]
    public async Task CustomerPaid_UnknownBuyerOrNothingOwed_SaysSo(string text, string key)
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await engine.HandleIncomingMessageAsync(Phone, "customer add Bilal, Model Town, 03001234567", default);

        await engine.HandleIncomingMessageAsync(Phone, text, default);

        Assert.Contains(_sent, m => m == MessageTemplates.Get(key, key.EndsWith("nocustomer") ? "Sara" : "Bilal"));
    }

    [Theory]
    [InlineData("kitne paise baqi hain")]
    [InlineData("kis ka paisa baqi hai")]
    public async Task MoneyOwed_InPlainWords_ShowsTheUnpaidList(string text)
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950");

        await engine.HandleIncomingMessageAsync(Phone, text, default);

        Assert.Equal("mark 1 paid", Assert.Single(LastListRows()).Id);
    }

    [Theory]
    [InlineData("Lawn Suit 3 Piece - 4500")]
    [InlineData("Lawn Suit 3 Piece - 4500, cost 3000, stock 15")]
    public void PieceCountInTheName_IsTheItem_NotAPack(string line)
    {
        var cmd = CommandParser.TryParse(line);
        Assert.Equal(CommandKind.AddProduct, cmd!.Kind);
        Assert.Equal(("Lawn Suit 3 Piece", "piece", 1m), (cmd.Product!.Name, cmd.Product.UnitType, cmd.Product.UnitQty));
    }

    [Fact]
    public async Task ReportsMenu_OffersTheBusinessAudit()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "menu", default);
        await engine.HandleIncomingMessageAsync(Phone, Assert.Single(LastListRows(), r => r.Title.Contains("Reports")).Id, default);

        var audit = Assert.Single(LastListRows(), r => r.Id == "business audit");
        Assert.NotNull(CommandParser.TryParse(audit.Id));
        Assert.True(LastListRows().Count <= 10);
    }

    [Fact]
    public async Task SetupEnds_WithTheOptionsListAndTheSalesQuestion_GuideIsARow()
    {
        using var db = _dbFactory.CreateContext();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ShortcutButtons = false, IndustryQuestion = true });
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Zara Boutique", default);
        _sent.Clear();
        _lists.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "1", default);

        Assert.True((await db.Sellers.SingleAsync()).OnboardingComplete);
        Assert.Equal(2, _sent.Count);
        Assert.Contains(_lists[0].SelectMany(s => s.Rows), r => r.Id == "guide");
        Assert.Contains(_lists[0].SelectMany(s => s.Rows), r => r.Id == "new order");
    }

    [Fact]
    public async Task DailyClose_At9pm_SendsTodaysCloseOnce_ToABusySeller()
    {
        using var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ali Store", OnboardingComplete = true, SalesMode = "delivery", Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ShortcutButtons = false, SuggestionButtons = true, ProductConfirmation = true, DailyCloseNudge = true });
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950, cost 880");
        _sent.Clear();

        var nowUtc = DateTime.UtcNow;
        var ninePmUtc = (nowUtc + TimeSpan.FromHours(5)).Date.AddHours(21) - TimeSpan.FromHours(5);
        var at = nowUtc > ninePmUtc ? nowUtc : ninePmUtc; // never earlier than the orders, so they still count as "today"
        if ((at + TimeSpan.FromHours(5)).Date != (nowUtc + TimeSpan.FromHours(5)).Date) return; // the test ran too close to midnight PKT

        await engine.RunScheduledJobsAsync(at.AddMinutes(1));
        await engine.RunScheduledJobsAsync(at.AddMinutes(20));

        Assert.Single(_sent, m => m.StartsWith("🌙"));
        Assert.Contains(_sent, m => m.StartsWith(MessageTemplates.Get("scheduled.dayclose.owed", 1, "Rs.1,900")));
    }

    [Fact]
    public async Task DailyClose_Before9pm_SendsNothing()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950");
        _sent.Clear();
        var eightPmUtc = (DateTime.UtcNow + TimeSpan.FromHours(5)).Date.AddHours(20) - TimeSpan.FromHours(5);

        await engine.RunScheduledJobsAsync(eightPmUtc);

        Assert.DoesNotContain(_sent, m => m.StartsWith("🌙"));
    }

    [Fact]
    public async Task TapOrder_QuantityAndAnotherProduct_AllInOneOrder()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await AddProductAsync(engine, "Kurta White - 2200, size M");
        await AddProductAsync(engine, "Lawn Suit 3 Piece - 4500");
        await engine.HandleIncomingMessageAsync(Phone, "customer add Sara, Gulberg, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);

        await engine.HandleIncomingMessageAsync(Phone, LastListRows().Single(r => r.Title.StartsWith("Lawn")).Id, default);
        await engine.HandleIncomingMessageAsync(Phone, ConversationEngine.CartOneMore, default);          // 2 suits
        await engine.HandleIncomingMessageAsync(Phone, ConversationEngine.CartAnotherProduct, default);
        await engine.HandleIncomingMessageAsync(Phone, LastListRows().Single(r => r.Title.StartsWith("Kurta")).Id, default);
        await engine.HandleIncomingMessageAsync(Phone, "3", default);                                    // 3 kurtas
        await engine.HandleIncomingMessageAsync(Phone, ConversationEngine.CartDone, default);
        await engine.HandleIncomingMessageAsync(Phone, "✅ Save", default);

        var order = await db.Orders.Include(o => o.Items).SingleAsync();
        Assert.Equal(2 * 4500m + 3 * 2200m, order.Total);
        Assert.Equal(new[] { 2, 3 }, order.Items.OrderBy(i => i.UnitPrice).Select(i => i.Quantity).Reverse().ToArray());
    }

    [Fact]
    public async Task PartPayment_InPlainWords_AsksYesWithTheBalance_ThenRecordsIt()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950");

        await engine.HandleIncomingMessageAsync(Phone, "Bilal ne 500 diye", default);
        Assert.Equal(0, (await db.Orders.SingleAsync()).AmountPaid);           // nothing before YES
        Assert.Contains(_sent, m => m.Contains("500") && m.Contains("1,400"));  // 1,900 - 500 left
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var order = await db.Orders.SingleAsync();
        Assert.Equal(500, order.AmountPaid);
        Assert.NotEqual(PaymentStatus.Paid, order.PaymentStatus);
    }

    [Fact]
    public async Task MarkById_IgnoresAnOpenList()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950");
        await engine.HandleIncomingMessageAsync(Phone, "customer add Sara, Gulberg, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);
        await engine.HandleIncomingMessageAsync(Phone, "Chini 5 kg 1", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var saraOrder = await db.Orders.Include(o => o.Customer).SingleAsync(o => o.Customer!.Name == "Sara");
        await engine.HandleIncomingMessageAsync(Phone, "unpaid orders", default);                   // list: 1 = Bilal, 2 = Sara

        await engine.HandleIncomingMessageAsync(Phone, $"mark #{saraOrder.Id} shipped", default);

        Assert.Equal(OrderStatus.Shipped, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == saraOrder.Id)).Status);
        Assert.Equal(1, await db.Orders.CountAsync(o => o.Status == OrderStatus.Shipped));
    }

    [Fact]
    public async Task DayClose_ShowsTips_LowStockAndRoughProfit()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await SellTwoChiniAsync(engine, "Chini 5 kg - 950, cost 880, stock 4");
        await AddProductAsync(engine, "Daal 1 kg - 330");
        await engine.HandleIncomingMessageAsync(Phone, "order for Bilal", default);
        await engine.HandleIncomingMessageAsync(Phone, "Daal 1 kg 1", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "aaj ka hisaab", default);

        var close = Assert.Single(_sent, m => m.Contains("hisaab —"));
        Assert.Contains(MessageTemplates.Get("day-close.tip.lowstock", "Chini (5 kg) (2)"), close);
        Assert.Contains(MessageTemplates.Get("day-close.rough", 1, 2, "Rs.140"), close);
    }

    [Theory]
    [InlineData("Kurta White - 2200, size S/M/L")]
    [InlineData("Kurta White - 2200, size S M L")]
    public async Task Sizes_InOneLine_AreOneProductPerSize_SavedTogether(string line)
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, line, default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var sizes = await db.Products.Select(p => p.Size).ToListAsync();
        Assert.Equal(new[] { "L", "M", "S" }, sizes.OrderBy(s => s).ToArray());
        Assert.All(await db.Products.ToListAsync(), p => Assert.Equal(2200m, p.Price));
    }

    [Fact]
    public async Task ProductWithoutCost_PreviewAsksForIt_AndCostBeforeSaveIsKept()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "Kurta - 2200", default);
        Assert.Contains(MessageTemplates.Get("product.cost.ask"), _sent);
        await engine.HandleIncomingMessageAsync(Phone, "cost 1,400", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(1400m, (await db.Products.SingleAsync()).CostPrice);
    }

    [Fact]
    public async Task AddProductTapped_SeveralLines_ThenKeepsAddingAfterSave()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "add product", default);
        await engine.HandleIncomingMessageAsync(Phone, "kurta 2200\nshalwar 900", default);   // several lines save at once
        await engine.HandleIncomingMessageAsync(Phone, "dupatta 600", default);   // no tap needed: still adding
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);  // ends the chain
        await engine.HandleIncomingMessageAsync(Phone, "topi 300", default);

        Assert.Equal(new[] { "dupatta", "kurta", "shalwar" }, (await db.Products.Select(p => p.Name).ToListAsync()).OrderBy(n => n).ToArray());
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
        var close = Assert.Single(_sent, m => m.Contains("hisaab —"));
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

        var close = Assert.Single(_sent, m => m.Contains("hisaab —"));
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

        var close = Assert.Single(_sent, m => m.Contains("hisaab —"));
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
        await engine.HandleIncomingMessageAsync(Phone, ConversationEngine.CartDone, default);
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
        await engine.HandleIncomingMessageAsync(Phone, ConversationEngine.CartDone, default);
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
