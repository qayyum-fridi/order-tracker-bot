using OrderTrackerBot.Application.Time;
using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class SqliteSchemaPatcherTests
{
    [Fact]
    public void Apply_AddsMissingColumnsToAnOldDatabase_AndIsSafeToRunTwice()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        foreach (var table in new[] { "Products", "Customers", "Orders" })
        {
            using var create = connection.CreateCommand();
            create.CommandText = $"CREATE TABLE \"{table}\" (Id INTEGER PRIMARY KEY)";
            create.ExecuteNonQuery();
        }
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = new AppDbContext(options);

        SqliteSchemaPatcher.Apply(db);
        SqliteSchemaPatcher.Apply(db);

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Products') WHERE name IN ('Category','Size','Color','Sku','StockQty')";
        Assert.Equal(5L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Orders') WHERE name IN ('DeliveryDate','OrderSource','Notes')";
        Assert.Equal(3L, check.ExecuteScalar());
    }
}

public class ConversationEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<ICatalogSheetImporter> _catalogSheets = new();
    private readonly List<string> _sentMessages = new();

    public ConversationEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sentMessages.Add(text))
            .Returns(Task.CompletedTask);
        // Clarifications are tap lists / buttons: their body counts as a sent message unless a test captures them itself.
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sentMessages.Add(body))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sentMessages.Add(body))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, catalogSheets: _catalogSheets.Object);

    private async Task OnboardSellerAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        await engine.HandleIncomingMessageAsync(Phone, "10 ke qareeb", default);
        await engine.HandleIncomingMessageAsync(Phone, "Lawn Suit - 3500", default);
        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1800", default);
        await engine.HandleIncomingMessageAsync(Phone, "done", default);
        _sentMessages.Clear();
    }

    [Fact]
    public async Task Onboarding_CompletesAndSavesCatalog()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);

        var seller = await db.Sellers.Include(s => s.Products).FirstAsync(s => s.WhatsAppPhoneNumber == Phone);
        Assert.True(seller.OnboardingComplete);
        Assert.Equal("Ayesha Collections", seller.BusinessName);
        Assert.Equal(2, seller.Products.Count);
    }

    [Fact]
    public async Task ResetAccount_RecognizesReversedWordOrder_AccountReset()
    {
        Assert.Equal(CommandKind.ResetAccount, CommandParser.TryParse("Account reset")!.Kind);
        Assert.Equal(CommandKind.ResetAccount, CommandParser.TryParse("reset account")!.Kind);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ResetAccount_WithConfirmation_WipesDataAndRestartsOnboarding()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var seller = await db.Sellers.Include(s => s.Session).FirstAsync(s => s.WhatsAppPhoneNumber == Phone);
        Assert.False(seller.OnboardingComplete);
        Assert.Null(seller.BusinessName);
        Assert.Equal(ConversationState.OnboardingLanguage, seller.Session!.State);
        Assert.Equal(0, await db.Products.CountAsync());
        Assert.Contains(_sentMessages, m => m.Contains("Order Assistant"));
        _sender.Verify(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(l => l.Count == 3), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ResetAccount_WithoutConfirmation_KeepsData()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "no", default);

        Assert.Equal(2, await db.Products.CountAsync());
        Assert.True((await db.Sellers.FirstAsync()).OnboardingComplete);
    }

    [Fact]
    public async Task Menu_SendsTappableList_WhoseRowsAreAllValidCommands()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        IReadOnlyList<MenuSection>? sent = null;
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, _, _, sections, _) => sent = sections)
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "menu", default);

        Assert.NotNull(sent);
        var rows = sent!.SelectMany(s => s.Rows).ToList();
        Assert.InRange(rows.Count, 1, 10);
        Assert.All(rows, r => Assert.NotNull(CommandParser.TryParse(r.Id)));
    }

    [Fact]
    public async Task BareProductLine_AddsProductWithoutPrefix_AndIsCaseInsensitiveOnUpdate()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "Sharara-4200", default);
        await engine.HandleIncomingMessageAsync(Phone, "sharara - 4500", default);

        var products = await db.Products.Where(p => p.Name.ToLower() == "sharara").ToListAsync();
        Assert.Single(products);
        Assert.Equal(4500m, products[0].Price);
    }

    [Fact]
    public async Task MultiLineProductList_AddsAllProductsAtOnce()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "Dupatta - 900\nScarf - 600\nShawl - 2500", default);

        Assert.Equal(5, await db.Products.CountAsync());
        Assert.Contains(_sentMessages, m => m.Contains("3 products save ho gaye"));
    }

    [Fact]
    public void CatalogSheetLink_IsRecognizedAsImportCommand()
    {
        var command = CommandParser.TryParse("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOp/edit?gid=0#gid=0");
        Assert.Equal(CommandKind.ImportCatalogSheet, command!.Kind);
        Assert.StartsWith("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOp", command.Text);
    }

    [Fact]
    public async Task ImportCatalogSheet_AddsEachRowAsAProduct()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        _catalogSheets.Setup(s => s.FetchProductLinesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "Dupatta - 900", "Scarf - 600" });

        await engine.HandleIncomingMessageAsync(Phone, "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOp/edit", default);

        Assert.Equal(4, await db.Products.CountAsync());
        Assert.Contains(_sentMessages, m => m.Contains("2 products import ho gaye"));
    }

    [Fact]
    public async Task ImportCatalogSheet_WhenFetchFails_RepliesGracefully_WithoutThrowing()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        _catalogSheets.Setup(s => s.FetchProductLinesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>?)null);

        await engine.HandleIncomingMessageAsync(Phone, "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOp/edit", default);

        Assert.Contains(_sentMessages, m => m.Contains("load nahi ho sake"));
        Assert.Equal(2, await db.Products.CountAsync());
    }

    [Theory]
    [InlineData("Sara, 1 kurti, 03009876543, Gulberg Lahore")]
    [InlineData("new order: Nimra, 1 kurti, 03211112233")]
    public void OrderText_IsNotMistakenForProductLine(string message)
    {
        Assert.False(CommandParser.TryParseProductLine(message, out _, out _));
    }

    [Fact]
    public async Task Help_SendsTappableList_WithinWhatsAppLimits()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        string? body = null;
        IReadOnlyList<MenuSection>? sent = null;
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, text, _, sections, _) => { body = text; sent = sections; })
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "help", default);

        Assert.NotNull(sent);
        var rows = sent!.SelectMany(s => s.Rows).ToList();
        Assert.InRange(rows.Count, 1, 10);
        Assert.All(rows, r => Assert.NotNull(CommandParser.TryParse(r.Id)));
        Assert.All(rows, r => Assert.True(r.Title.Length <= 24));
        Assert.True(body!.Length <= 1024);
        Assert.DoesNotContain(rows, r => r.Id == "undo");
    }

    private async Task SeedHalfEnteredOrderAsync(AppDbContext db, string missingField)
    {
        var session = await db.Sessions.FirstAsync();
        session.State = ConversationState.AwaitingOrderMissingFields;
        session.ContextJson = new SessionContextData
        {
            PendingOrder = new PendingOrderData { Items = { new PendingOrderItemData { ProductName = "Lawn Suit", UnitPrice = 3500 } } },
            PendingMissingField = missingField
        }.ToJson();
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("no")]
    [InlineData("Cancel")]
    [InlineData("nahi")]
    public async Task HalfEnteredOrder_CancelWord_AbandonsDraft_NotSavedAsName(string word)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedHalfEnteredOrderAsync(db, "CustomerName");
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, word, default);

        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sentMessages, m => m.Contains("order cancel kar diya"));
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task HalfEnteredOrder_Command_LeavesDraftAndRunsCommand()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedHalfEnteredOrderAsync(db, "Phone");
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);

        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sentMessages, m => m.Contains("Adhoora order chhod diya"));
        Assert.Contains(_sentMessages, m => m.Contains("Aapka Catalog"));
    }

    [Fact]
    public async Task HalfEnteredOrder_InvalidPhone_IsRejected()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedHalfEnteredOrderAsync(db, "Phone");
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "abc", default);

        Assert.Equal(ConversationState.AwaitingOrderMissingFields, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sentMessages, m => m.Contains("Phone number sahi nahi lagta"));
    }

    [Theory]
    [InlineData("add discount", "create discount: EID10")]
    [InlineData("ek HBL 50 ka discount naya bana dein", "create discount: EID10")]
    [InlineData("Naya discount banain.", "create discount: EID10")]
    [InlineData("نیا ڈسکاؤنٹ بنائیں۔", "create discount: EID10")]
    [InlineData("discount bana do", "create discount: EID10")]
    [InlineData("ڈسکاؤنٹ نیا بنا دیں", "create discount: EID10")]
    [InlineData("New Product", "Kurti - 1800")]
    [InlineData("add payment", "add payment: jazzcash")]
    [InlineData("create loyalty", "create loyalty: 5 orders")]
    [InlineData("add tracking", "add tracking: Leopards")]
    public async Task HowToPhrases_ShowExactFormat_InsteadOfAskingTheAi(string message, string expectedFragment)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, message, default);

        Assert.Contains(_sentMessages, m => m.Contains(expectedFragment));
        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Clarification_WithLongOptions_IsATapList_WhoseRowsAreTheirNumbers()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        IReadOnlyList<MenuSection>? sent = null;
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, _, _, sections, _) => sent = sections)
            .Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = false,
                ClarificationQuestion = "Mujhe samajh nahi aaya 🤔 Kya aap:",
                ClarificationOptions = { "Naya order add karna chahte hain", "Kisi order ka status update karna chahte hain" }
            });

        await engine.HandleIncomingMessageAsync(Phone, "wo waala order kal tak bhej dena", default);

        var rows = sent!.SelectMany(s => s.Rows).ToList();
        Assert.Equal(new[] { "1", "2", "menu" }, rows.Select(r => r.Id));
        Assert.All(rows, r => Assert.True(r.Title.Length <= 24, r.Title));

        await engine.HandleIncomingMessageAsync(Phone, "2", default); // the tapped row arrives as its id
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sentMessages, m => m.Contains("mark 3 shipped"));
    }

    [Fact]
    public async Task DescribedNewDiscount_AiCreateDiscountIntent_StartsDiscountFlow()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "create_discount" });

        await engine.HandleIncomingMessageAsync(Phone, "ye ek naya discount hai jo ke product ke liye istemal hoga.", default);

        Assert.Equal(ConversationState.AwaitingDiscountDetails, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sentMessages, m => m.Contains("create discount: EID10"));
        Assert.DoesNotContain(_sentMessages, m => m.Contains("sirf orders/sales"));
    }

    [Fact]
    public void KeywordClarificationOptions_AreAllRunnableCommands()
    {
        foreach (var area in ConversationEngine.CommandKeywordAreas)
            foreach (var option in area.Options)
                Assert.True(CommandParser.TryParse(option) is not null, $"\"{option}\" is not a parseable command");
    }

    [Theory]
    [InlineData("ye ek naya discount hai jo ke product ke liye istemal hoga.", "Discount")]
    [InlineData("kharcha ka kuch karna hai", "Kharche")]
    [InlineData("mera stock kitna hai", "Stock")]
    [InlineData("ڈسکاؤنٹ کے بارے میں", "Discount")]
    public void TryKeywordClarification_PicksTheFeatureNamed(string text, string questionFragment)
    {
        Assert.True(ConversationEngine.TryKeywordClarification(text, out var question, out var options));
        Assert.Contains(questionFragment, question);
        Assert.NotEmpty(options);
    }

    [Fact]
    public void TryKeywordClarification_SmallTalk_HasNoMatch() =>
        Assert.False(ConversationEngine.TryKeywordClarification("bohat thak gayi hoon aaj", out _, out _));

    [Fact]
    public async Task VoiceStyleSentenceWithFeatureKeyword_AsksWhichCommand_ThenRunsThePickedOne()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "off_topic" });
        IReadOnlyList<string>? buttons = null;
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, b, _) => { _sentMessages.Add(body); buttons = b; })
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "ye ek naya discount hai jo ke product ke liye istemal hoga.", default);

        Assert.Equal(ConversationState.AwaitingClarificationChoice, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sentMessages, m => m.Contains("Discount ke baare mein"));
        Assert.Equal(new[] { "create discount", "discount list", "discount performance" }, buttons); // one tap each, no typing
        Assert.DoesNotContain(_sentMessages, m => m.Contains("sirf orders/sales"));

        await engine.HandleIncomingMessageAsync(Phone, "create discount", default); // the tapped button arrives as its label

        Assert.Equal(ConversationState.AwaitingDiscountDetails, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sentMessages, m => m.Contains("create discount: EID10"));
    }

    [Fact]
    public async Task DiscountList_WhenEmpty_ShowsHowToCreateOne()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "discount list", default);

        Assert.Contains(_sentMessages, m => m.Contains("create discount: EID10"));
    }

    [Fact]
    public async Task AddDiscount_GuidedFlow_CodeThenValue_CreatesDiscount()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "add discount", default);
        await engine.HandleIncomingMessageAsync(Phone, "Welcome50", default);
        Assert.Contains(_sentMessages, m => m.Contains("Ab kitna discount"));
        await engine.HandleIncomingMessageAsync(Phone, "Rs.50 flat", default);

        var discount = await db.Discounts.SingleAsync();
        Assert.Equal("WELCOME50", discount.Code);
        Assert.Equal(50m, discount.Value);
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("EID10, 10%", 10)]
    [InlineData("EID10, 10 percent", 10)]
    [InlineData("EID10, 50 rupees", 50)]
    public async Task AddDiscount_AcceptsFullSpecInOneMessage_WithForgivingValueFormats(string spec, int expectedValue)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "add discount", default);
        await engine.HandleIncomingMessageAsync(Phone, spec, default);

        Assert.Equal(expectedValue, (int)(await db.Discounts.SingleAsync()).Value);
    }

    [Fact]
    public async Task AddDiscount_CancelWordLeavesFlow_WithoutCreatingAnything()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "add discount", default);
        await engine.HandleIncomingMessageAsync(Phone, "cancel", default);

        Assert.Equal(0, await db.Discounts.CountAsync());
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    private async Task<List<int>> SeedTwoPendingOrdersAsync(AppDbContext db)
    {
        var seller = await db.Sellers.FirstAsync();
        var customer = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        // Burn ids so list position != order id, which is exactly the mix-up screen 5c fixes.
        for (var i = 0; i < 4; i++)
        {
            db.Orders.Add(new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, Status = OrderStatus.Delivered, PaymentStatus = PaymentStatus.Paid });
        }
        await db.SaveChangesAsync();
        var pending = new List<OrderTrackerBot.Domain.Entities.Order>();
        for (var i = 0; i < 2; i++)
        {
            var o = new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, Status = OrderStatus.Pending };
            db.Orders.Add(o);
            pending.Add(o);
        }
        await db.SaveChangesAsync();
        return pending.Select(o => o.Id).ToList();
    }

    [Fact]
    public async Task MarkByListNumber_UsesPositionInLastList_NotOrderId()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var pendingIds = await SeedTwoPendingOrdersAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "pending orders", default);
        await engine.HandleIncomingMessageAsync(Phone, "mark 2 shipped", default);

        var shipped = await db.Orders.Where(o => o.Status == OrderStatus.Shipped).ToListAsync();
        Assert.Single(shipped);
        Assert.Equal(pendingIds[1], shipped[0].Id);
        Assert.Contains(_sentMessages, m => m.Contains("last list"));
    }

    [Fact]
    public async Task StatusPicker_ListsTheStatusesAnOrderCanMoveTo_AsRunnableRows()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var pendingIds = await SeedTwoPendingOrdersAsync(db);
        var engine = CreateEngine(db);
        string? body = null;
        IReadOnlyList<MenuSection>? sent = null;
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, text, _, sections, _) => { body = text; sent = sections; })
            .Returns(Task.CompletedTask);
        var id = pendingIds[0];

        await engine.HandleIncomingMessageAsync(Phone, $"status {id}", default);

        var rows = sent!.SelectMany(s => s.Rows).ToList();
        Assert.Contains($"Order #{id}", body);
        Assert.Contains(rows, r => r.Id == $"mark {id} shipped");
        Assert.Contains(rows, r => r.Id == $"mark {id} delivered");
        Assert.Contains(rows, r => r.Id == $"cancel order {id}");
        Assert.DoesNotContain(rows, r => r.Id == $"mark {id} returned"); // a pending order was never sent out
        Assert.All(rows, r => Assert.NotNull(CommandParser.TryParse(r.Id)));
        Assert.All(rows, r => Assert.True(r.Title.Length <= 24, r.Title));
    }

    [Fact]
    public async Task StatusPicker_ForACancelledOrder_SaysItCannotChange()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var pendingIds = await SeedTwoPendingOrdersAsync(db);
        (await db.Orders.FirstAsync(o => o.Id == pendingIds[0])).Status = OrderStatus.Cancelled;
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"status {pendingIds[0]}", default);

        Assert.Contains(_sentMessages, m => m.Contains("CANCELLED") && m.Contains("change nahi ho sakta"));
    }

    [Fact]
    public async Task MarkByNumber_WithNoRecentList_UsesRealOrderId()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var pendingIds = await SeedTwoPendingOrdersAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"mark {pendingIds[0]} shipped", default);

        Assert.Equal(OrderStatus.Shipped, (await db.Orders.FindAsync(pendingIds[0]))!.Status);
    }

    [Theory]
    [InlineData("odrers todya")]
    [InlineData("pendng orders")]
    [InlineData("catlog")]
    [InlineData("آج کے آرڈرز")]
    public async Task TypoAndUrduAliases_RunTheRightCommand(string message)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, message, default);

        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotEmpty(_sentMessages);
    }

    [Theory]
    [InlineData("👍")]
    [InlineData("ok")]
    [InlineData("Haan")]
    [InlineData("✅")]
    public void ShortAndEmojiReplies_CountAsYes(string reply) => Assert.True(CommandParser.IsAffirmative(reply));

    [Fact]
    public async Task ShareCatalog_ListsProductsReadyToForward()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "share catalog", default);

        Assert.Contains(_sentMessages, m => m.Contains("Lawn Suit - Rs.3,500") && m.Contains("customer ko bhej dein"));
    }

    [Theory]
    [InlineData("audio", "Voice message")]
    [InlineData("image", "screenshot")]
    public async Task UnsupportedMedia_GetsAFriendlyTextOnlyReply(string type, string fragment)
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);

        await engine.HandleUnsupportedMediaAsync(Phone, type, default);

        Assert.Contains(_sentMessages, m => m.Contains(fragment) && m.Contains("TEXT"));
    }

    [Theory]
    [InlineData("add product (detailed)", "product")]
    [InlineData("Add Customer (Detailed)", "customer")]
    [InlineData("new order (detailed)", "order")]
    public async Task DetailedForm_OpensTheFlow_WhenConfigured(string message, string expectedKind)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        _sender.Setup(s => s.SendFlowMessageAsync(Phone, expectedKind, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await engine.HandleIncomingMessageAsync(Phone, message, default);

        _sender.Verify(s => s.SendFlowMessageAsync(Phone, expectedKind, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain(_sentMessages, m => m.Contains("setup nahi hua"));
    }

    [Fact]
    public async Task DetailedForm_WhenNotConfigured_FallsBackToTypedFormat()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "add product (detailed)", default);

        Assert.Contains(_sentMessages, m => m.Contains("setup nahi hua") && m.Contains("Kurti - 1800"));
    }

    [Fact]
    public async Task ProductForm_SavesVariantDetails_AndUpdatesSameVariantInsteadOfDuplicating()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        var json = "{\"flow_token\":\"product\",\"name\":\"Lawn Suit\",\"category\":\"Lawn\",\"price\":\"3500\",\"size\":\"M\",\"color\":\"Red\",\"stock\":\"12\"}";

        await engine.HandleFlowSubmissionAsync(Phone, json, default);
        await engine.HandleFlowSubmissionAsync(Phone, json.Replace("3500", "3800"), default);

        var product = await db.Products.SingleAsync(p => p.Color == "Red");
        Assert.Equal((3800m, "M", "Lawn", 12), (product.Price, product.Size, product.Category, product.StockQty));
        Assert.Contains(_sentMessages, m => m.Contains("Lawn Suit (Red, M)") && m.Contains("stock 12"));
    }

    [Fact]
    public async Task CustomerForm_SavesDetails_AndUpsertsByPhone()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        var json = "{\"flow_token\":\"customer\",\"name\":\"Ramsha\",\"phone\":\"03211234567\",\"city\":\"Lahore\",\"address\":\"Johar Town\",\"preferred_contact\":\"WhatsApp\",\"notes\":\"Lal color pasand hai\"}";

        await engine.HandleFlowSubmissionAsync(Phone, json, default);
        await engine.HandleFlowSubmissionAsync(Phone, json.Replace("Lahore", "Karachi"), default);

        var customer = await db.Customers.SingleAsync();
        Assert.Equal(("Ramsha", "Karachi", "Johar Town", "Lal color pasand hai"), (customer.Name, customer.City, customer.Address, customer.Notes));
    }

    [Fact]
    public async Task OrderForm_CreatesOrderWithDeliverySourceAndPayment()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        var json = "{\"flow_token\":\"order\",\"customer\":\"Ramsha\",\"product\":\"Lawn Suit\",\"quantity\":\"2\",\"payment_method\":\"COD\",\"delivery_date\":\"2026-09-28\",\"order_source\":\"Instagram\",\"notes\":\"Gift wrap chahiye\"}";

        await engine.HandleFlowSubmissionAsync(Phone, json, default);

        var order = await db.Orders.Include(o => o.Items).SingleAsync();
        Assert.Equal(7000m, order.Total);
        Assert.Equal(OrderPaymentMethod.Cod, order.PaymentMethod);
        Assert.Equal(new DateTime(2026, 9, 28), order.DeliveryDate!.Value.Date);
        Assert.Equal(("Instagram", "Gift wrap chahiye"), (order.OrderSource, order.Notes));
        Assert.Contains(_sentMessages, m => m.Contains("Order saved") && m.Contains("delivery 28 Sep"));
    }

    [Fact]
    public async Task OrderForm_UnknownProduct_RepliesInsteadOfSavingAnything()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleFlowSubmissionAsync(Phone, "{\"flow_token\":\"order\",\"customer\":\"Ramsha\",\"product\":\"Sharara\",\"quantity\":\"1\"}", default);

        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Contains(_sentMessages, m => m.Contains("catalog mein nahi mila"));
    }

    [Theory]
    [InlineData("{\"name\":\"Kurti\",\"price\":\"1800\"}")]
    [InlineData("{\"flow_token\":\"unused\",\"name\":\"Kurti\",\"price\":\"1800\"}")]
    public async Task ProductForm_IsRecognizedFromItsFields_WhenFlowTokenIsMissingOrForeign(string json)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleFlowSubmissionAsync(Phone, json, default);

        Assert.Contains(await db.Products.ToListAsync(), p => p.Name == "Kurti" && p.Price == 1800m);
    }

    [Fact]
    public async Task FlowSubmission_WithGarbageJson_IsIgnoredSafely()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleFlowSubmissionAsync(Phone, "not json", default);
        await engine.HandleFlowSubmissionAsync(Phone, "{\"flow_token\":\"unknown\"}", default);

        Assert.Empty(_sentMessages);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("reports")]
    [InlineData("catalog")]
    [InlineData("payments")]
    [InlineData("discounts")]
    [InlineData("customers")]
    public async Task MenuCategory_OpensTappableSubMenu_WithBackRow_AndOnlyRunnableRows(string category)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        string? body = null;
        IReadOnlyList<MenuSection>? sent = null;
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, text, _, sections, _) => { body = text; sent = sections; })
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, $"menu {category}", default);

        var rows = sent!.SelectMany(s => s.Rows).ToList();
        Assert.InRange(rows.Count, 2, 10);
        Assert.Contains(rows, r => r.Id == "menu");
        Assert.All(rows, r => Assert.NotNull(CommandParser.TryParse(r.Id)));
        Assert.All(rows, r => Assert.True(r.Title.Length <= 24, r.Title));
        Assert.True(body!.Length <= 1024);
        Assert.DoesNotContain(rows, r => r.Id == "undo");
    }

    [Fact]
    public async Task MainMenu_ListsSevenCategories_AndHelpShowsOnlyTheEightCoreThings()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        var lists = new List<(string Body, IReadOnlyList<MenuSection> Sections)>();
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, text, _, sections, _) => lists.Add((text, sections)))
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "menu", default);
        await engine.HandleIncomingMessageAsync(Phone, "help", default);

        Assert.Equal(7, lists[0].Sections.SelectMany(s => s.Rows).Count(r => r.Id.StartsWith("menu ")));
        Assert.Contains("Main Menu", lists[0].Body);
        Assert.Contains("📊 Reports", lists[0].Body);
        Assert.Contains("Yeh commands try karein", lists[1].Body);
        Assert.Contains("trending products", lists[1].Body);
        Assert.Contains("\"guide\"", lists[1].Body);
    }

    [Fact]
    public async Task Guide_OffersTopics_ThenWalksFourStepsWithNext()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        var buttons = new List<string>();
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, text, _, _) => buttons.Add(text))
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "guide", default);
        await engine.HandleIncomingMessageAsync(Phone, "Poora guide", default);
        await engine.HandleIncomingMessageAsync(Phone, "Next ➜", default);
        await engine.HandleIncomingMessageAsync(Phone, "Next ➜", default);
        await engine.HandleIncomingMessageAsync(Phone, "Next ➜", default);

        Assert.Contains("Guide kahan se shuru karein?", buttons[0]);
        Assert.Contains("Step 1/4", buttons[1]);
        Assert.Contains("Step 2/4", buttons[2]);
        Assert.Contains("Step 3/4", buttons[3]);
        Assert.Contains(_sentMessages, m => m.Contains("Step 4/4"));
    }

    [Fact]
    public async Task CustomerList_Detail_AndSearch_ShowOrdersAndSpend()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedTwoPendingOrdersAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "customer list", default);
        Assert.Contains(_sentMessages, m => m.Contains("Aapke Customers (1 total)") && m.Contains("1 Sara - 03001112222 - 6 orders"));

        await engine.HandleIncomingMessageAsync(Phone, "customer 1", default);
        Assert.Contains(_sentMessages, m => m.Contains("👤 Sara") && m.Contains("Total orders: 6") && m.Contains("Loyal customer"));

        await engine.HandleIncomingMessageAsync(Phone, "search customer: 0300111", default);
        Assert.Contains(_sentMessages, m => m.Contains("1 customer mila") && m.Contains("Sara"));

        await engine.HandleIncomingMessageAsync(Phone, "customer nobody", default);
        Assert.Contains(_sentMessages, m => m.Contains("koi customer nahi mila"));
    }

    [Fact]
    public async Task CustomerFeedbackCommand_IsNotMistakenForCustomerDetail()
    {
        Assert.Equal(CommandKind.CustomerFeedbackList, CommandParser.TryParse("customer feedback")!.Kind);
        Assert.Equal(CommandKind.CustomerList, CommandParser.TryParse("customer list")!.Kind);
        Assert.Equal(CommandKind.CustomerDetail, CommandParser.TryParse("customer 2")!.Kind);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CatalogSizeStep_OffersButtons_SoUserCanTapInsteadOfTypingWrongInput()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        IReadOnlyList<string>? labels = null;
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, l, _) => labels = l)
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);

        Assert.Equal(new[] { "Chhota (20 se kam)", "Bara (20+)" }, labels);
        Assert.Equal(ConversationState.OnboardingCatalogSize, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task CatalogSizeStep_TappingLargeButton_OffersBulkPaste()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Bara (20+)", default);

        Assert.Contains(_sentMessages, m => m.Contains("ek saath kai products"));
    }

    [Fact]
    public async Task AfterAddingProduct_OffersAddAnotherOrDoneButtons_AndDoneButtonFinishesOnboarding()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        IReadOnlyList<string>? labels = null;
        string? body = null;
        string? listBody = null;
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, l, _) => { body = b; labels = l; })
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, b, _, _, _) => listBody = b)
            .Returns(Task.CompletedTask);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        await engine.HandleIncomingMessageAsync(Phone, "Chhota (20 se kam)", default);

        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1800", default);
        Assert.Equal(new[] { "➕ Aur product", "📋 Catalog dekhein", "✅ Done" }, labels);
        Assert.Contains("✅ Add ho gaya: Kurti - Rs.1,800", body);
        Assert.Contains("Catalog mein ab 1 product hain", body);

        await engine.HandleIncomingMessageAsync(Phone, "📋 Catalog dekhein", default);
        Assert.Contains("1. Kurti - Rs.1,800", body);

        await engine.HandleIncomingMessageAsync(Phone, "✅ Done", default);

        var seller = await db.Sellers.FirstAsync();
        Assert.True(seller.OnboardingComplete);
        Assert.Equal(1, await db.Products.CountAsync());
        Assert.Contains("Aage kya karna hai?", listBody);
    }

    [Fact]
    public async Task MidOnboardingCommand_IsDeferredNotExecuted()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        await engine.HandleIncomingMessageAsync(Phone, "10 ke qareeb", default);
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.Contains(_sentMessages, m => m.Contains("pehle catalog complete karein"));
        var orderCount = await db.Orders.CountAsync();
        Assert.Equal(0, orderCount);
    }

    [Fact]
    public async Task OnboardingOptionalDetailsStep_HelpOrMenu_StillExecutes_AndStepResumesAfter()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "help", default);
        Assert.DoesNotContain(_sentMessages, m => m.Contains("pehle setup mukammal karein"));
        Assert.Equal(ConversationState.OnboardingOptionalDetails, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "menu", default);
        Assert.DoesNotContain(_sentMessages, m => m.Contains("pehle setup mukammal karein"));
        Assert.Equal(ConversationState.OnboardingOptionalDetails, (await db.Sessions.FirstAsync()).State);

        // The optional-details step is still open afterward.
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        Assert.Equal(ConversationState.OnboardingCatalogSize, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task MidOnboardingHelpOrMenu_StillExecutes_AndCatalogStepResumesAfter()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        await engine.HandleIncomingMessageAsync(Phone, "10 ke qareeb", default);
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "help", default);
        Assert.DoesNotContain(_sentMessages, m => m.Contains("pehle catalog complete karein"));

        await engine.HandleIncomingMessageAsync(Phone, "menu", default);
        Assert.DoesNotContain(_sentMessages, m => m.Contains("pehle catalog complete karein"));

        // Onboarding catalog step is still open afterward — a product line still adds to the catalog.
        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1800", default);
        Assert.Equal(ConversationState.OnboardingAddProduct, (await db.Sessions.FirstAsync()).State);
        Assert.Equal(1, await db.Products.CountAsync());
    }

    [Fact]
    public async Task MidOnboardingStockCommand_SetsStock_AndCatalogStepStaysOpen()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        await engine.HandleIncomingMessageAsync(Phone, "10 ke qareeb", default);
        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1800", default);
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "stock Kurti 20", default);

        Assert.DoesNotContain(_sentMessages, m => m.Contains("pehle catalog complete karein"));
        Assert.Equal(20, (await db.Products.SingleAsync()).StockQty);
        Assert.Equal(ConversationState.OnboardingAddProduct, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task CatalogSizeStep_OffersQuickActionsList_AlongsideTheProductPrompt()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        IReadOnlyList<MenuSection>? sections = null;
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, _, _, s, _) => sections = s)
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        await engine.HandleIncomingMessageAsync(Phone, "Chhota (20 se kam)", default);

        var rows = sections!.SelectMany(s => s.Rows).ToList();
        Assert.Equal(new[] { "setup", "new order", "guide", "connect instagram" }, rows.Select(r => r.Id));
        Assert.All(rows, r => Assert.NotNull(CommandParser.TryParse(r.Id)));
    }

    [Fact]
    public async Task MidOnboardingGuideOrSetup_StillExecutes_AndCatalogStepResumesAfter()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        await engine.HandleIncomingMessageAsync(Phone, "Chhota (20 se kam)", default);
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "setup", default);
        Assert.DoesNotContain(_sentMessages, m => m.Contains("pehle catalog complete karein"));
        Assert.Equal(ConversationState.OnboardingAddProduct, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "new order", default);
        Assert.DoesNotContain(_sentMessages, m => m.Contains("pehle catalog complete karein"));
        Assert.Equal(ConversationState.OnboardingAddProduct, (await db.Sessions.FirstAsync()).State);

        // Opening the guide mid-catalog-step and closing it again must resume the catalog step,
        // not drop to Idle (an incomplete seller landing on Idle would restart onboarding).
        await engine.HandleIncomingMessageAsync(Phone, "guide", default);
        Assert.Equal(ConversationState.AwaitingGuideStep, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "Sirf orders", default);
        Assert.Equal(ConversationState.OnboardingAddProduct, (await db.Sessions.FirstAsync()).State);

        // The catalog step is genuinely still open — a product line still adds to the catalog.
        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1800", default);
        Assert.Equal(1, await db.Products.CountAsync());
    }

    private async Task StartCatalogStepAsync(ConversationEngine engine)
    {
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "skip", default);
        await engine.HandleIncomingMessageAsync(Phone, "Chhota (20 se kam)", default);
        _sentMessages.Clear();
    }

    [Fact]
    public async Task OrderTextDuringCatalogStep_IsHandedToOrderFlow_NotRejected()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await StartCatalogStepAsync(engine);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order",
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Ayesha",
                    Phone = "03001234567",
                    Items = { new AiOrderItemDraft { ProductName = "Lawn Suit", Quantity = 2 } }
                }
            });

        await engine.HandleIncomingMessageAsync(Phone,
            "Ayesha 2 lawn suit aur 1 kurti, 0300-1234567, Gulberg Lahore, jazzcash advance, code EID10", default);

        Assert.DoesNotContain(_sentMessages, m => m.Contains("samajh nahi aaya"));
        Assert.True((await db.Sellers.FirstAsync()).OnboardingComplete);
        // Unmatched product -> the normal order flow offers to add it to the catalog.
        Assert.Equal(ConversationState.AwaitingOrderMissingFields, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sentMessages, m => m.Contains("catalog mein nahi mila"));
    }

    [Fact]
    public async Task NonOrderTextDuringCatalogStep_StillGetsHelpfulHint()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await StartCatalogStepAsync(engine);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", IsOrderAttempt = false });

        await engine.HandleIncomingMessageAsync(Phone, "hmm kya likhoon", default);

        Assert.Contains(_sentMessages, m => m.Contains("samajh nahi aaya") && m.Contains("Order darj"));
        Assert.False((await db.Sellers.FirstAsync()).OnboardingComplete);
        Assert.Equal(ConversationState.OnboardingAddProduct, (await db.Sessions.FirstAsync()).State);
    }

    private async Task SetSellerLanguageAsync(AppDbContext db, string language)
    {
        (await db.Sellers.FirstAsync()).PreferredLanguage = language;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UrduSeller_GetsRepliesTranslated_ListsKeepRowIds_ButButtonLabelsStayAsIs()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SetSellerLanguageAsync(db, Lang.UrduScript);
        var engine = CreateEngine(db);
        _ai.Setup(a => a.TranslateAsync(It.IsAny<IReadOnlyList<string>>(), Lang.UrduScript, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, string _, CancellationToken _) => (IReadOnlyList<string>?)texts.Select(t => "UR:" + t).ToList());
        string? listBody = null;
        IReadOnlyList<MenuSection>? listSections = null;
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, sections, _) => { listBody = body; listSections = sections; })
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);
        await engine.HandleIncomingMessageAsync(Phone, "naya order", default);

        Assert.Contains(_sentMessages, m => m.StartsWith("UR:"));
        Assert.StartsWith("UR:", listBody);
        Assert.All(listSections!.SelectMany(s => s.Rows), r => Assert.NotNull(CommandParser.TryParse(r.Id)));
    }

    [Fact]
    public async Task TranslatedButtonLabels_AreShown_AndATapIsMappedBackToTheOriginalLabel()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SetSellerLanguageAsync(db, Lang.UrduScript);
        var engine = CreateEngine(db);
        _ai.Setup(a => a.TranslateAsync(It.IsAny<IReadOnlyList<string>>(), Lang.UrduScript, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, string _, CancellationToken _) => (IReadOnlyList<string>?)texts.Select(t => "UR:" + t).ToList());
        IReadOnlyList<string>? labels = null;
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, l, _) => labels = l)
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "cod pending", default);

        Assert.Equal(new[] { "UR:All", "UR:3+ days", "UR:7+ days" }, labels);
        Assert.Equal(ConversationState.AwaitingRuntimeFilterChoice, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "UR:3+ days", default);

        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task Translation_ThatChangesANumber_IsDiscarded_AndTheOriginalIsSent()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SetSellerLanguageAsync(db, Lang.English);
        var engine = CreateEngine(db);
        _ai.Setup(a => a.TranslateAsync(It.IsAny<IReadOnlyList<string>>(), Lang.English, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> texts, string _, CancellationToken _) => (IReadOnlyList<string>?)texts.Select(t => "EN:" + t + " 777").ToList());

        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);

        Assert.Contains(_sentMessages, m => m.Contains("Lawn Suit") && m.Contains("3,500"));
        Assert.DoesNotContain(_sentMessages, m => m.StartsWith("EN:"));
    }

    [Fact]
    public async Task RomanUrduSeller_IsNeverTranslated()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);

        _ai.Verify(a => a.TranslateAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OrderTextDuringCatalogStep_WhenAiUnavailable_SaysSoInsteadOfFormatHint()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await StartCatalogStepAsync(engine);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", IsOrderAttempt = false, AiUnavailable = true });

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha 2 lawn suit aur 1 kurti, 0300-1234567, Gulberg Lahore", default);

        Assert.Contains(_sentMessages, m => m.Contains("AI service available nahi"));
        Assert.DoesNotContain(_sentMessages, m => m.Contains("Maazrat, samajh nahi aaya"));
        Assert.False((await db.Sellers.FirstAsync()).OnboardingComplete);
        Assert.Equal(ConversationState.OnboardingAddProduct, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task OrderLineWithPhoneNumber_IsNeverSavedAsCatalogProduct()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await StartCatalogStepAsync(engine);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", IsOrderAttempt = false });

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha, 2 suit, 0300-1234567", default);

        Assert.Equal(0, await db.Products.CountAsync());
    }

    [Theory]
    [InlineData("Kurti: 1800", "Kurti", 1800)]
    [InlineData("Kurti = Rs 1800", "Kurti", 1800)]
    [InlineData("Lawn Suit - 3500 rs", "Lawn Suit", 3500)]
    public async Task CatalogStep_AcceptsLooserProductLines(string line, string name, int price)
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await StartCatalogStepAsync(engine);

        await engine.HandleIncomingMessageAsync(Phone, line, default);

        var product = await db.Products.SingleAsync();
        Assert.Equal(name, product.Name);
        Assert.Equal(price, product.Price);
    }

    [Theory]
    [InlineData("connect instagram")]
    [InlineData("instagram connect")]
    public void ConnectInstagram_RecognizesEitherWordOrder(string message)
    {
        Assert.Equal(CommandKind.ConnectInstagram, CommandParser.TryParse(message)!.Kind);
    }

    [Fact]
    public async Task OrdersToday_WithNoOrders_RepliesNoOrders()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.Contains(_sentMessages, m => m.Contains("Aaj koi order nahi aaya"));
    }

    [Theory]
    [InlineData("profit", "30d")]
    [InlineData("profit today", "today")]
    [InlineData("Profit last month", "lastmonth")]
    [InlineData("profit week", "7d")]
    [InlineData("munafa", "30d")]
    public void Parses_Profit(string text, string period)
    {
        var cmd = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.Profit, cmd.Kind);
        Assert.Equal(period, cmd.Text);
    }

    [Fact]
    public void ProfitMarginQuestion_IsNotAProfitCommand() =>
        Assert.NotEqual(CommandKind.Profit, CommandParser.TryParse("profit margin on kurti")?.Kind);

    [Fact]
    public async Task Profit_UsesSnapshotCost_FallsBackToProductCost_AndSkipsOrdersWithoutCost()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var seller = await db.Sellers.FirstAsync();
        var customer = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        var withCost = new OrderTrackerBot.Domain.Entities.Product { SellerId = seller.Id, Name = "Kurti", Price = 400, CostPrice = 200 };
        var noCost = new OrderTrackerBot.Domain.Entities.Product { SellerId = seller.Id, Name = "Dupatta", Price = 300 };
        db.AddRange(customer, withCost, noCost);
        await db.SaveChangesAsync();

        OrderTrackerBot.Domain.Entities.Order Make(OrderStatus status, decimal subtotal, decimal discount, params OrderTrackerBot.Domain.Entities.OrderItem[] items)
        {
            var o = new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, Status = status, Subtotal = subtotal, DiscountAmount = discount, DeliveryCharge = 200, Total = subtotal - discount + 200 };
            foreach (var i in items) o.Items.Add(i);
            return o;
        }
        // Snapshot cost 300 x2 @500, Rs.100 discount: sales 900, cost 600.
        db.Orders.Add(Make(OrderStatus.Pending, 1000, 100, new OrderTrackerBot.Domain.Entities.OrderItem { ProductNameSnapshot = "Suit", UnitPrice = 500, UnitCost = 300, Quantity = 2 }));
        // No snapshot (older order): falls back to the product's current cost 200: sales 400, cost 200.
        db.Orders.Add(Make(OrderStatus.Delivered, 400, 0, new OrderTrackerBot.Domain.Entities.OrderItem { ProductId = withCost.Id, ProductNameSnapshot = "Kurti", UnitPrice = 400, Quantity = 1 }));
        // Cost unknown anywhere: left out and reported.
        db.Orders.Add(Make(OrderStatus.Pending, 300, 0, new OrderTrackerBot.Domain.Entities.OrderItem { ProductId = noCost.Id, ProductNameSnapshot = "Dupatta", UnitPrice = 300, Quantity = 1 }));
        // Cancelled: ignored entirely.
        db.Orders.Add(Make(OrderStatus.Cancelled, 5000, 0, new OrderTrackerBot.Domain.Entities.OrderItem { ProductNameSnapshot = "Suit", UnitPrice = 5000, UnitCost = 1, Quantity = 1 }));
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "profit", default);

        Assert.Contains(_sentMessages, m => m.Contains("Orders: 2") && m.Contains("Sales (delivery ke baghair): Rs.1,300")
            && m.Contains("Cost: Rs.800") && m.Contains("Profit: Rs.500 (38.5%)") && m.Contains("1 order(s) shamil nahi"));
    }

    [Fact]
    public async Task Profit_WithNoCostAnywhere_ExplainsHowToAddIt()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var seller = await db.Sellers.FirstAsync();
        var customer = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var order = new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, Subtotal = 500, Total = 500 };
        order.Items.Add(new() { ProductNameSnapshot = "Suit", UnitPrice = 500, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "profit", default);

        Assert.Contains(_sentMessages, m => m.Contains("cost save nahi hai") && m.Contains("cost 1200"));
    }

    [Fact]
    public async Task OrdersYesterday_ListsOnlyYesterdaysOrders()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var seller = await db.Sellers.FirstAsync();
        var customer = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var (yStart, _) = OrderTrackerBot.Application.Time.SellerClock.LocalDayRangeUtc(seller.TimeZoneId, DateTime.UtcNow, -1);
        db.Orders.Add(new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, CreatedAt = yStart.AddHours(2) });
        db.Orders.Add(new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "kal ke orders", default);

        Assert.Contains(_sentMessages, m => m.Contains("Yesterday's Orders (1)"));
    }

    [Fact]
    public async Task Receipt_SendsPdfDocumentToSeller_WithOrderDetails()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var seller = await db.Sellers.FirstAsync();
        var customer = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var order = new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, Subtotal = 3500, Total = 3500 };
        order.Items.Add(new OrderTrackerBot.Domain.Entities.OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var pdf = new Mock<IReceiptPdfGenerator>();
        ReceiptData? captured = null;
        pdf.Setup(p => p.Generate(It.IsAny<ReceiptData>())).Callback<ReceiptData>(d => captured = d).Returns(new byte[] { 1, 2, 3 });
        _sender.Setup(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), It.IsAny<string>(), "application/pdf", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, receiptPdf: pdf.Object);

        await engine.HandleIncomingMessageAsync(Phone, $"receipt {order.Id}", default);

        _sender.Verify(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), $"Receipt-{order.Id}.pdf", "application/pdf",
            It.Is<string?>(c => c!.Contains("Sara") && c.Contains("3,500")), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Sara", captured!.CustomerName);
        Assert.Equal("Ayesha Collections", captured.BusinessName);
        Assert.Single(captured.Lines);
        Assert.Contains(_sentMessages, m => m.Contains("Forward") && m.Contains("https://wa.me/923001112222"));
    }

    [Fact]
    public async Task Branding_LogoCaptionedImage_IsSaved_AppearsOnReceipt_AndCanBeRemoved()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var seller = await db.Sellers.FirstAsync();
        var customer = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var order = new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, Subtotal = 100, Total = 100 };
        order.Items.Add(new OrderTrackerBot.Domain.Entities.OrderItem { ProductNameSnapshot = "Pen", UnitPrice = 100, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var logo = new byte[] { 10, 20, 30 };
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync("media-1", It.IsAny<CancellationToken>())).ReturnsAsync((logo, "image/jpeg"));
        var pdf = new Mock<IReceiptPdfGenerator>();
        pdf.Setup(p => p.CanEmbedImage(logo)).Returns(true);
        ReceiptData? captured = null;
        pdf.Setup(p => p.Generate(It.IsAny<ReceiptData>())).Callback<ReceiptData>(d => captured = d).Returns(new byte[] { 1 });
        _sender.Setup(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: media.Object, receiptPdf: pdf.Object);

        await engine.HandleImageMessageAsync(Phone, "media-1", "logo", default);
        Assert.Contains(_sentMessages, m => m.Contains("Logo save ho gaya"));
        Assert.Equal(logo, (await db.SellerBrandings.SingleAsync()).Logo);
        _ai.Verify(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Never);

        await engine.HandleIncomingMessageAsync(Phone, "receipt", default);
        Assert.Equal(logo, captured!.Logo);
        Assert.Null(captured.Banner);

        await engine.HandleIncomingMessageAsync(Phone, "remove logo", default);
        Assert.Null((await db.SellerBrandings.SingleAsync()).Logo);
        await engine.HandleIncomingMessageAsync(Phone, "receipt", default);
        Assert.Null(captured!.Logo);
    }

    [Fact]
    public async Task Branding_UnusableImage_IsRejected_AndNothingSaved()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var junk = new byte[] { 1, 2 };
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync("m", It.IsAny<CancellationToken>())).ReturnsAsync((junk, "image/jpeg"));
        var pdf = new Mock<IReceiptPdfGenerator>();
        pdf.Setup(p => p.CanEmbedImage(junk)).Returns(false);
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: media.Object, receiptPdf: pdf.Object);

        await engine.HandleImageMessageAsync(Phone, "m", "banner", default);

        Assert.Contains(_sentMessages, m => m.Contains("Banner ke taur par nahi lag sakti"));
        Assert.Empty(db.SellerBrandings);
    }

    [Fact]
    public async Task Branding_FreeTextWish_ExplainsBothLogoAndBanner()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, receiptPdf: Mock.Of<IReceiptPdfGenerator>());

        await engine.HandleIncomingMessageAsync(Phone, "receipt par apni image lagani hai", default);

        Assert.Contains(_sentMessages, m => m.Contains("Logo") && m.Contains("Banner") && m.Contains("Photo/Gallery"));
        _ai.Verify(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Receipt_SuggestsLogoAndBanner_OnFirstTwoReceipts_ThenStops()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var seller = await db.Sellers.FirstAsync();
        var customer = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.Orders.Add(new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, Total = 100 });
        await db.SaveChangesAsync();
        var pdf = new Mock<IReceiptPdfGenerator>();
        pdf.Setup(p => p.Generate(It.IsAny<ReceiptData>())).Returns(new byte[] { 1 });
        _sender.Setup(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, receiptPdf: pdf.Object);

        var hinted = new List<bool>();
        for (var i = 0; i < 3; i++)
        {
            _sentMessages.Clear();
            await engine.HandleIncomingMessageAsync(Phone, "receipt", default);
            hinted.Add(_sentMessages.Any(m => m.Contains("logo ya banner lagana chahte hain")));
        }

        Assert.Equal(new[] { true, true, false }, hinted);
    }

    [Fact]
    public async Task Branding_HelpAndRemove_ReportCurrentState()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, receiptPdf: Mock.Of<IReceiptPdfGenerator>());

        await engine.HandleIncomingMessageAsync(Phone, "logo", default);
        await engine.HandleIncomingMessageAsync(Phone, "remove banner", default);

        Assert.Contains(_sentMessages, m => m.Contains("Receipt Logo: abhi set nahi") && m.Contains("Caption mein \"logo\""));
        Assert.Contains(_sentMessages, m => m.Contains("Banner pehle se set nahi hai"));
    }

    private (ConversationEngine Engine, List<(string File, byte[] Bytes, string? Caption)> Files) CreateExportEngine(AppDbContext db)
    {
        var files = new List<(string, byte[], string?)>();
        _sender.Setup(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], string, string, string?, CancellationToken>((_, bytes, name, _, caption, _) => files.Add((name, bytes, caption)))
            .ReturnsAsync(true);
        return (new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object,
            exportWriter: new OrderTrackerBot.Infrastructure.Export.ExportXlsxWriter()), files);
    }

    private async Task SeedExportDataAsync(AppDbContext db)
    {
        var seller = await db.Sellers.FirstAsync();
        var sara = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222", City = "Lahore" };
        var gone = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Deleted Dan", DeletedAt = DateTime.UtcNow };
        var injected = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "=HYPERLINK(\"http://x\")", Phone = "03005556666" };
        db.Customers.AddRange(sara, gone, injected);
        await db.SaveChangesAsync();
        var order = new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = sara.Id, Subtotal = 5300, DiscountAmount = 300, Total = 5000, DiscountCode = "EID" };
        order.Items.Add(new OrderTrackerBot.Domain.Entities.OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 1 });
        order.Items.Add(new OrderTrackerBot.Domain.Entities.OrderItem { ProductNameSnapshot = "Kurti", UnitPrice = 1800, Quantity = 1 });
        var old = new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = sara.Id, Total = 700, CreatedAt = DateTime.UtcNow.AddDays(-90) };
        old.Items.Add(new OrderTrackerBot.Domain.Entities.OrderItem { ProductNameSnapshot = "Dupatta", UnitPrice = 700, Quantity = 1 });
        db.Orders.AddRange(order, old);
        db.Discounts.Add(new OrderTrackerBot.Domain.Entities.Discount { SellerId = seller.Id, Code = "EID10", Type = OrderTrackerBot.Domain.Enums.DiscountType.Percent, Value = 10 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Export_Orders_SendsWorkbookWithOrdersAndItems_FilteredByPeriod()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedExportDataAsync(db);
        var (engine, files) = CreateExportEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "export orders 30 days", default);

        var (name, bytes, caption) = Assert.Single(files);
        Assert.Matches(@"^Ayesha-Collections-export-\d{4}-\d{2}-\d{2}\.xlsx$", name);
        Assert.Contains("Orders (30 din) (1)", caption);
        using var stream = new MemoryStream(bytes);
        Assert.Equal(new[] { "Orders", "Order Items", "Guide" }, MiniExcelLibs.MiniExcel.GetSheetNames(stream).ToArray());
        var orders = MiniExcelLibs.MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: true, sheetName: "Orders").Cast<IDictionary<string, object>>().ToList();
        var row = Assert.Single(orders);
        Assert.Equal("Sara", row["Customer"]);
        Assert.Equal("1 x Lawn Suit; 1 x Kurti", row["Items"]);
        Assert.Equal("03001112222", row["Phone"]);
        var items = MiniExcelLibs.MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: true, sheetName: "Order Items").Cast<IDictionary<string, object>>().ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(_sentMessages, m => m.Contains("Excel file ready"));
    }

    [Fact]
    public async Task Export_Everything_OneWorkbook_SkipsDeletedCustomers_AndKeepsTextAsText()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedExportDataAsync(db);
        var (engine, files) = CreateExportEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "export all", default);

        var (_, bytes, _) = Assert.Single(files);
        Assert.Equal(new[] { "Orders", "Order Items", "Customers", "Catalog", "Discounts", "Loyalty Rules", "Guide" },
            MiniExcelLibs.MiniExcel.GetSheetNames(new MemoryStream(bytes)).ToArray());
        var customers = MiniExcelLibs.MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: true, sheetName: "Customers").Cast<IDictionary<string, object>>().ToList();
        Assert.DoesNotContain(customers, c => (string)c["Name"] == "Deleted Dan");
        Assert.Equal(2, customers.Count);
        var sara = customers.Single(c => (string)c["Name"] == "Sara");
        Assert.Equal(2d, Convert.ToDouble(sara["Orders"]));
        Assert.Equal(5700d, Convert.ToDouble(sara["Total spent"]));
        // A buyer-supplied "=..." name stays a plain string cell (never a formula).
        Assert.Contains(customers, c => (string)c["Name"] == "=HYPERLINK(\"http://x\")");
        var catalog = MiniExcelLibs.MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: true, sheetName: "Catalog").Cast<IDictionary<string, object>>().ToList();
        Assert.Equal(2, catalog.Count); // the two products from onboarding
    }

    [Fact]
    public async Task Export_WithoutChoice_SendsPickerList_AndNoFile()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var (engine, files) = CreateExportEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "export", default);

        Assert.Empty(files);
        _sender.Verify(s => s.SendListMessageAsync(Phone, It.Is<string>(b => b.Contains("Export")), It.IsAny<string>(),
            It.Is<IReadOnlyList<MenuSection>>(sec => sec.Single().Rows.Any(r => r.Id == "export all") && sec.Single().Rows.All(r => r.Title.Length <= 24)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Export_WhenNothingToExport_SaysSo_AndSendsNoFile()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var (engine, files) = CreateExportEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "export orders", default);

        Assert.Empty(files);
        Assert.Contains(_sentMessages, m => m.Contains("koi data nahi hai"));
    }

    private ConversationEngine CreateShortcutEngine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, features: new FeatureOptions());

    private void VerifyBar(string[] labels, Times times) =>
        _sender.Verify(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(l => l.SequenceEqual(labels)), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task ShortcutBar_FollowsAFinishedReply_WithIntroOnlyTheFirstTime()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateShortcutEngine(db);
        var bodies = new List<string>();
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => bodies.Add(body)).Returns(Task.CompletedTask);
        _sender.Invocations.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        VerifyBar(new[] { "📋 Menu", "➕ Naya order", "📦 Orders today" }, Times.Exactly(2));
        Assert.Contains("shortcut off", bodies[0]);
        Assert.Equal("⚡ Quick actions", bodies[1]);
    }

    [Fact]
    public async Task ShortcutBar_NotSentAfterMenuOrWhileSomethingIsPending()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateShortcutEngine(db);
        _sender.Invocations.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "menu", default);          // already an interactive list
        await engine.HandleIncomingMessageAsync(Phone, "reset account", default); // waiting for yes/no

        VerifyBar(new[] { "📋 Menu", "➕ Naya order", "📦 Orders today" }, Times.Never());
    }

    [Fact]
    public async Task ShortcutBar_CanBeSwitchedOffAndOn()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateShortcutEngine(db);
        var bar = new[] { "📋 Menu", "➕ Naya order", "📦 Orders today" };
        _sender.Invocations.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "shortcut off", default);
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);
        VerifyBar(bar, Times.Never());
        Assert.Contains(_sentMessages, m => m.Contains("Shortcut buttons band"));

        await engine.HandleIncomingMessageAsync(Phone, "shortcut on", default);
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);
        VerifyBar(bar, Times.AtLeast(2));
    }

    [Fact]
    public async Task ShortcutBar_UsesUrduLabelsForUrduSellers_AndRespectsTheFeatureFlag()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        (await db.Sellers.FirstAsync()).PreferredLanguage = "urdu_script";
        await db.SaveChangesAsync();
        _sender.Invocations.Clear();

        await CreateShortcutEngine(db).HandleIncomingMessageAsync(Phone, "orders today", default);
        VerifyBar(new[] { "📋 مینو", "➕ نیا آرڈر", "📦 آج کے آرڈرز" }, Times.Once());

        _sender.Invocations.Clear();
        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "orders today", default); // features unset = off
        VerifyBar(new[] { "📋 مینو", "➕ نیا آرڈر", "📦 آج کے آرڈرز" }, Times.Never());
    }

    [Fact]
    public async Task Receipt_ForUnknownOrder_RepliesNotFound_AndSendsNothing()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, receiptPdf: Mock.Of<IReceiptPdfGenerator>());

        await engine.HandleIncomingMessageAsync(Phone, "receipt 999", default);

        Assert.Contains(_sentMessages, m => m.Contains("Order #999 nahi mila"));
        _sender.Verify(s => s.SendDocumentAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("03001112222", "https://wa.me/923001112222")]
    [InlineData("923001112222", "https://wa.me/923001112222")]
    [InlineData("+92 300 1112222", "https://wa.me/923001112222")]
    [InlineData("12345", null)]
    [InlineData(null, null)]
    public void ChatLink_NormalisesPakistaniNumbers(string? phone, string? expected) =>
        Assert.Equal(expected, ConversationEngine.ChatLink(phone));

    [Fact]
    public async Task FreeformMessage_WhenAiFindsNoOrder_AsksClarification()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = false,
                ClarificationQuestion = "Mujhe samajh nahi aaya 🤔 Kya aap:",
                ClarificationOptions = { "Naya order add karna chahte hain", "Kisi order ka status update karna chahte hain" }
            });

        await engine.HandleIncomingMessageAsync(Phone, "wo waala order kal tak bhej dena", default);

        Assert.Contains(_sentMessages, m => m.Contains("Mujhe samajh nahi aaya"));

        var session = await db.Sessions.FirstAsync();
        Assert.Equal(ConversationState.AwaitingClarificationChoice, session.State);
    }

    [Theory]
    [InlineData("Order dena hai", "order ki tafseel")]
    [InlineData("Kisi order ka status update karna chahte hain", "naya status")]
    [InlineData("Product ke baare mein poochna hai", "catalog")]
    [InlineData("Kuch aur", "kya karna hai")]
    public async Task ClarificationChoice_ReplyMatchesChosenOption(string option, string expectedFragment)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { IsOrderAttempt = false, ClarificationQuestion = "Kya aap:", ClarificationOptions = { option } });
        await engine.HandleIncomingMessageAsync(Phone, "lon suit", default);

        _sentMessages.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "1", default);

        Assert.Contains(_sentMessages, m => m.Contains(expectedFragment));
        if (expectedFragment != "order ki tafseel")
            Assert.DoesNotContain(_sentMessages, m => m.Contains("order ki tafseel"));
    }

    [Fact]
    public async Task ResetAccount_WorksEvenWhenStuckInClarificationMenu()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = false,
                ClarificationQuestion = "Kya aap:",
                ClarificationOptions = { "Haan", "Nahi" }
            });
        await engine.HandleIncomingMessageAsync(Phone, "gibberish message", default);
        Assert.Equal(ConversationState.AwaitingClarificationChoice, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "Reset account", default);
        Assert.Equal(ConversationState.AwaitingResetConfirmation, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(ConversationState.OnboardingLanguage, (await db.Sessions.FirstAsync()).State);
    }

    [Theory]
    [InlineData("English", "english")]
    [InlineData("اردو", "urdu_script")]
    [InlineData("Roman Urdu", "roman_urdu")]
    public async Task FirstTouch_ShowsIntroThenSavesChosenLanguage(string reply, string expected)
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "hi", default);
        Assert.Contains(_sentMessages, m => m.Contains("Order Assistant"));
        Assert.Equal(ConversationState.OnboardingLanguage, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, reply, default);

        var seller = await db.Sellers.FirstAsync();
        Assert.Equal(expected, seller.PreferredLanguage);
        Assert.Equal(ConversationState.OnboardingStartChoice, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        Assert.Equal(ConversationState.OnboardingBusinessName, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task ChangeLanguage_SwitchesLanguage_AndOffersNextActionButtons()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        var buttons = new List<(string Text, IReadOnlyList<string> Labels)>();
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, text, labels, _) => buttons.Add((text, labels)))
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "change language", default);
        await engine.HandleIncomingMessageAsync(Phone, "English", default);

        Assert.Contains("Konsi language", buttons[0].Text);
        Assert.Contains("switching to English", buttons[1].Text);
        Assert.Equal(new[] { "📋 Menu", "📦 New Order", "📖 Guide" }, buttons[1].Labels);
        Assert.Equal("english", (await db.Sellers.FirstAsync()).PreferredLanguage);
    }

    [Fact]
    public async Task BusinessSetup_ShowsSummary_AndPaymentMethodButtonSavesNewMethod()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);
        var buttons = new List<string>();
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, text, _, _) => buttons.Add(text))
            .Returns(Task.CompletedTask);

        await engine.HandleIncomingMessageAsync(Phone, "⚙️ Business Setup", default);
        await engine.HandleIncomingMessageAsync(Phone, "Payment Method", default);
        await engine.HandleIncomingMessageAsync(Phone, "easypaisa, 0300-9876543", default);

        Assert.Contains("Business Setup — Ayesha Collections", buttons[0]);
        Assert.Contains("Language: Roman Urdu", buttons[0]);
        Assert.Contains(_sentMessages, m => m.Contains("Naya payment method bhejein"));
        Assert.Contains(_sentMessages, m => m.Contains("Easypaisa") && m.Contains("saved"));
    }

    [Fact]
    public async Task FreeformOrder_FullyResolved_GoesStraightToConfirmation_ThenSavesOnYes()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Sara",
                    Phone = "03009876543",
                    Address = "Gulberg Lahore",
                    Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } }
                }
            });

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03009876543, Gulberg Lahore", default);
        Assert.Contains(_sentMessages, m => m.Contains("Confirm order") && m.Contains("Sara"));

        _sentMessages.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Contains(_sentMessages, m => m.Contains("Order saved as PENDING"));
        var order = await db.Orders.Include(o => o.Items).Include(o => o.Customer).SingleAsync();
        Assert.Equal("Sara", order.Customer!.Name);
        Assert.Equal(1800m, order.Total);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public async Task DuplicateOrderConfirmation_HelpOrMenu_StillExecutes_AndPromptStaysOpen()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Sara",
                    Phone = "03009876543",
                    Address = "Gulberg Lahore",
                    Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } }
                }
            });

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03009876543, Gulberg Lahore", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03009876543, Gulberg Lahore", default);
        Assert.Contains(_sentMessages, m => m.Contains("isi jaisa order"));
        Assert.Equal(ConversationState.AwaitingDuplicateOrderConfirmation, (await db.Sessions.FirstAsync()).State);
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "help", default);
        Assert.DoesNotContain(_sentMessages, m => m.Contains("Reply 1 ya 2"));
        Assert.Equal(ConversationState.AwaitingDuplicateOrderConfirmation, (await db.Sessions.FirstAsync()).State);

        // The duplicate prompt is still open afterward — "1" still saves the second order.
        await engine.HandleIncomingMessageAsync(Phone, "1", default);
        Assert.Equal(2, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task MarkStatus_Shipped_ThenUndo_RevertsToPending()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Bilal",
                    Phone = "03001112222",
                    Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } }
                }
            });
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 1 kurti, 03001112222", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.SingleAsync();
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} shipped", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(OrderStatus.Shipped, order.Status);

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public async Task CreateDiscount_ThenAppliedOrder_TotalsReflectPercentOff()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "create discount: EID10, 10 percent", default);
        _sentMessages.Clear();

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Ayesha",
                    Phone = "03001234567",
                    DiscountCode = "EID10",
                    Items = { new AiOrderItemDraft { ProductName = "Lawn Suit", MatchedCatalogProductName = "Lawn Suit", Quantity = 2 } }
                }
            });

        await engine.HandleIncomingMessageAsync(Phone, "new order: Ayesha, 2x Lawn Suit, EID10", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var order = await db.Orders.SingleAsync();
        Assert.Equal(7000m, order.Subtotal);
        Assert.Equal(700m, order.DiscountAmount);
        Assert.Equal(6300m, order.Total);
    }

    [Fact]
    public async Task UnknownProduct_PromptsForPrice_ThenAddsToCatalogAndConfirmsOrder()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Hina",
                    Phone = "03211234567",
                    Items = { new AiOrderItemDraft { ProductName = "Sharara", MatchedCatalogProductName = null, Quantity = 1 } }
                }
            });

        await engine.HandleIncomingMessageAsync(Phone, "Hina, 1 sharara, 03211234567", default);
        Assert.Contains(_sentMessages, m => m.Contains("Sharara") && m.Contains("nahi mila"));

        _sentMessages.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "1", default);
        Assert.Contains(_sentMessages, m => m.Contains("price kya hai"));

        _sentMessages.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "4200", default);
        Assert.Contains(_sentMessages, m => m.Contains("Confirm order"));

        var product = await db.Products.SingleAsync(p => p.Name == "Sharara");
        Assert.Equal(4200m, product.Price);
    }

    // ---- "for how long": every report takes any period ----

    private async Task<OrderTrackerBot.Domain.Entities.Order> SeedOrderAsync(AppDbContext db, string customerName, double daysAgo, decimal total = 1000m,
        OrderStatus status = OrderStatus.Delivered, string? discountCode = null, decimal discount = 0m, DateTime? deliveryDate = null)
    {
        var seller = await db.Sellers.FirstAsync();
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.SellerId == seller.Id && c.Name == customerName);
        if (customer is null)
        {
            customer = new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = customerName, Phone = "0300" + Math.Abs(customerName.GetHashCode() % 10000000).ToString("D7") };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        var order = new OrderTrackerBot.Domain.Entities.Order
        {
            SellerId = seller.Id, CustomerId = customer.Id, Status = status, PaymentStatus = PaymentStatus.Unpaid, Total = total,
            DiscountCode = discountCode, DiscountAmount = discount, DeliveryDate = deliveryDate,
            CreatedAt = DateTime.UtcNow.AddDays(-daysAgo)
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    [Theory]
    [InlineData("orders last 7 days")]
    [InlineData("pichle 7 din ke orders")]
    [InlineData("آرڈرز پچھلے 7 دن")]
    [InlineData("orders 7 din")]
    public async Task OrdersForLastNDays_ListsOnlyOrdersInsideTheWindow(string message)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedOrderAsync(db, "Ayesha", daysAgo: 2);
        await SeedOrderAsync(db, "Bilal", daysAgo: 40);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, message, default);

        Assert.Contains(_sentMessages, m => m.Contains("Ayesha") && !m.Contains("Bilal"));
    }

    [Fact]
    public async Task OrdersForALongerWindow_IncludeOlderOrders_ButNotOlderThanThat()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedOrderAsync(db, "Ayesha", daysAgo: 2);
        await SeedOrderAsync(db, "Bilal", daysAgo: 40);
        await SeedOrderAsync(db, "Chand", daysAgo: 400);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "orders last 90 days", default);

        Assert.Contains(_sentMessages, m => m.Contains("Ayesha") && m.Contains("Bilal") && !m.Contains("Chand") && m.Contains("Last 90 days"));
    }

    [Fact]
    public async Task OrdersTomorrow_ListsWhatIsDueForDeliveryThen_NotOrdersPlacedThen()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var seller = await db.Sellers.FirstAsync();
        var tomorrowNoon = SellerClock.LocalToUtc(seller.TimeZoneId, SellerClock.LocalToday(seller.TimeZoneId, DateTime.UtcNow).AddDays(1).AddHours(12));
        await SeedOrderAsync(db, "Ayesha", daysAgo: 1, status: OrderStatus.Pending, deliveryDate: tomorrowNoon);
        await SeedOrderAsync(db, "Bilal", daysAgo: 1, status: OrderStatus.Delivered, deliveryDate: tomorrowNoon);
        await SeedOrderAsync(db, "Chand", daysAgo: 1, status: OrderStatus.Pending);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "orders tomorrow", default);

        Assert.Contains(_sentMessages, m => m.Contains("Delivery due") && m.Contains("Ayesha") && !m.Contains("Bilal") && !m.Contains("Chand"));
    }

    [Fact]
    public async Task OrdersForAPeriodWithNoOrders_SaysSoByName()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "orders last year", default);

        Assert.Contains(_sentMessages, m => m.Contains("Last year") && m.Contains("koi order nahi"));
    }

    [Fact]
    public async Task CustomersForAPeriod_RanksThoseWhoOrderedThen_BySpend()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedOrderAsync(db, "Ayesha", daysAgo: 2, total: 500);
        await SeedOrderAsync(db, "Ayesha", daysAgo: 3, total: 700);
        await SeedOrderAsync(db, "Bilal", daysAgo: 1, total: 3000);
        await SeedOrderAsync(db, "Chand", daysAgo: 40, total: 9000);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "pichle hafte ke customers", default);

        Assert.Contains(_sentMessages, m => m.Contains("Last week") || m.Contains("Last"));
        await engine.HandleIncomingMessageAsync(Phone, "customers last 7 days", default);
        var reply = _sentMessages.Last();
        Assert.Contains("Ayesha", reply);
        Assert.Contains("2 orders", reply);
        Assert.DoesNotContain("Chand", reply);
        Assert.True(reply.IndexOf("Bilal", StringComparison.Ordinal) < reply.IndexOf("Ayesha", StringComparison.Ordinal), reply); // 3000 beats 1200
    }

    [Fact]
    public async Task DiscountPerformance_CanBeLimitedToAPeriod()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        await SeedOrderAsync(db, "Ayesha", daysAgo: 2, total: 900, discountCode: "EID10", discount: 100);
        await SeedOrderAsync(db, "Bilal", daysAgo: 40, total: 900, discountCode: "EID10", discount: 100);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "discounts last 7 days", default);
        Assert.Contains(_sentMessages, m => m.Contains("Discount Performance — Last 7 days") && m.Contains("EID10") && m.Contains("1 orders"));

        _sentMessages.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "discount report last 90 days", default);
        Assert.Contains(_sentMessages, m => m.Contains("EID10") && m.Contains("2 orders"));
    }

    [Theory]
    [InlineData("profit last quarter", "last quarter")]
    [InlineData("expenses last week", "last week")]
    [InlineData("net last quarter", "last quarter")]
    [InlineData("summary last year", "Last year")]
    public async Task OtherReports_AcceptAPeriod_AndNameItInTheReply(string message, string label)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, message, default);

        Assert.Contains(_sentMessages, m => m.Contains(label, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_sentMessages, m => m.Contains("Samajh nahi aaya"));
    }

    [Theory]
    [InlineData("create discount: EID10, 10 percent, expires 2 weeks", 14)]
    [InlineData("create discount: EID10, 10 percent, expires 2 hafte", 14)]
    [InlineData("create discount: EID10, 10 percent, expires 15 days", 15)]
    [InlineData("create discount: EID10, Rs.50 flat, expires 1 month", 30)]
    public async Task CreateDiscount_ExpiryDurations(string message, int approxDays)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, message, default);

        var discount = await db.Discounts.SingleAsync();
        Assert.InRange((discount.ExpiresAt!.Value - DateTime.UtcNow).TotalDays, approxDays - 2, approxDays + 1);
    }

    [Fact]
    public async Task CreateDiscount_ExpiryDate_ExpiresAtTheEndOfThatDay()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "create discount: NEWYEAR, 20 percent, expires 31 Dec", default);

        var expires = (await db.Discounts.SingleAsync()).ExpiresAt!.Value;
        var seller = await db.Sellers.FirstAsync();
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(expires, DateTimeKind.Utc), SellerClock.Resolve(seller.TimeZoneId));
        Assert.Equal(new DateTime(local.Year, 1, 1, 0, 0, 0), local); // midnight after 31 Dec
        Assert.Equal(1, local.Day);
    }

    [Fact]
    public async Task CreateDiscount_ExpiryNotUnderstood_CreatesNothingAndShowsExamples()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "create discount: EID10, 10 percent, expires someday", default);

        Assert.Empty(await db.Discounts.ToListAsync());
        Assert.Contains(_sentMessages, m => m.Contains("Expiry samajh nahi aayi") && m.Contains("expires 2 weeks"));
    }

    [Fact]
    public async Task TrendingProducts_CustomPeriod_AcceptsAnyPeriodPhrase()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "trending products", default);
        await engine.HandleIncomingMessageAsync(Phone, "custom", default);
        Assert.Equal(ConversationState.AwaitingCustomDateRange, (await db.Sessions.FirstAsync()).State);
        await engine.HandleIncomingMessageAsync(Phone, "pichle 3 mahine", default);

        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
        Assert.DoesNotContain(_sentMessages, m => m.Contains("samajh nahi aayi"));
    }

    [Fact]
    public async Task TrendingProducts_WithAPeriodInTheCommand_SkipsTheQuestion()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "trending products last quarter", default);

        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    public void Dispose() => _dbFactory.Dispose();
}
