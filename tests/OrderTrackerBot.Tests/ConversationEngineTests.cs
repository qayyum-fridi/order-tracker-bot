using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
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
    private readonly List<string> _sentMessages = new();

    public ConversationEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sentMessages.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

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
    public async Task OrdersToday_WithNoOrders_RepliesNoOrders()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.Contains(_sentMessages, m => m.Contains("Aaj koi order nahi aaya"));
    }

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

    public void Dispose() => _dbFactory.Dispose();
}
