using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Screens from the UI mockup that were added in the "match mockup + tech doc" pass.</summary>
public class MockupFeatureTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();
    private readonly List<string> _sent = new();
    private readonly List<(string Body, IReadOnlyList<string> Buttons)> _buttons = new();

    public MockupFeatureTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, buttons, _) => { _buttons.Add((body, buttons)); _sent.Add(body); })
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine Engine(AppDbContext db, BillingOptions? billing = null) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, billing, _media.Object);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db, BillingOptions? billing = null, params string[] products)
    {
        var engine = Engine(db, billing);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "10 ke qareeb" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        foreach (var p in products.Length == 0 ? new[] { "Lawn Suit - 3500", "Kurti - 1800" } : products)
            await engine.HandleIncomingMessageAsync(Phone, p, default);
        await engine.HandleIncomingMessageAsync(Phone, "done", default);
        _sent.Clear();
        _buttons.Clear();
        return engine;
    }

    private void AiReturns(AiMessageAnalysis analysis) =>
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

    private static AiMessageAnalysis Order(string name, string product, int qty, string? phone = "03001234567") => new()
    {
        Intent = "new_order",
        IsOrderAttempt = true,
        Order = new AiOrderDraft { CustomerName = name, Phone = phone, Items = { new AiOrderItemDraft { ProductName = product, MatchedCatalogProductName = product, Quantity = qty } } }
    };

    [Fact]
    public async Task Onboarding_SavesOptionalDetails_AndStartsFreeTrial()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        await engine.HandleIncomingMessageAsync(Phone, "Lahore, Clothing, @ayesha.collections", default);
        Assert.Contains(_sent, m => m.Contains("Noted — Lahore, Clothing business, @ayesha.collections"));

        foreach (var m in new[] { "10", "Lawn Suit - 3500", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);

        var seller = await db.Sellers.FirstAsync();
        Assert.Equal("Lahore", seller.City);
        Assert.Equal("Clothing", seller.BusinessType);
        Assert.Equal("@ayesha.collections", seller.InstagramHandle);
        Assert.NotNull(seller.TrialEndsAt);
        Assert.Contains(_sent, m => m.Contains("14-din FREE trial"));
    }

    [Fact]
    public async Task ExpiredTrial_PausesAccess_ThenPlanAndPaidReactivate()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db, new BillingOptions { PaymentNumber = "0300-0000000" });
        var seller = await db.Sellers.FirstAsync();
        seller.TrialEndsAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);
        Assert.Contains(_sent, m => m.Contains("free trial khatam ho gaya"));
        Assert.DoesNotContain(_sent, m => m.Contains("Today's Orders") || m.Contains("Aaj koi order"));

        await engine.HandleIncomingMessageAsync(Phone, "Basic - Rs.300", default);
        Assert.Contains(_sent, m => m.Contains("Basic plan select ho gaya") && m.Contains("0300-0000000"));

        await engine.HandleIncomingMessageAsync(Phone, "paid", default);
        seller = await db.Sellers.FirstAsync();
        Assert.Equal(SubscriptionPlan.Basic, seller.Plan);
        Assert.True(seller.SubscriptionActiveUntil > DateTime.UtcNow.AddDays(27));
        _founderAlerts.Verify(f => f.NotifyAsync(seller.Id, It.Is<string>(s => s.Contains("PAID")), It.IsAny<CancellationToken>()), Times.Once);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "loyal customers", default);
        Assert.Contains(_sent, m => m.Contains("Pro plan"));
    }

    [Fact]
    public async Task WeightProducts_QuickAdd_AndOrderShowsPackTotal()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "Sugar 5 kg - 500", default);
        Assert.Contains(_sent, m => m.Contains("Added: Sugar - 5kg - Rs.500"));
        var sugar = await db.Products.FirstAsync(p => p.Name == "Sugar");
        Assert.Equal("kg", sugar.UnitType);
        Assert.Equal(5m, sugar.UnitQty);

        AiReturns(Order("Bilal", "Sugar - 5kg", 2, "03001112222"));
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 2 sugar, 03001112222", default);
        Assert.Contains(_sent, m => m.Contains("Sugar (5kg pack) x2 = 10kg total") && m.Contains("Rs.1,000"));
    }

    [Fact]
    public async Task PriceTiers_PickTheRightRateForTheQuantity()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "Dakao Kaju - price tiers: 1kg=320, 10kg=300, 25kg=280", default);
        Assert.Contains(_sent, m => m.Contains("1-9 kg: Rs.320/kg") && m.Contains("10-24 kg: Rs.300/kg") && m.Contains("25+ kg: Rs.280/kg"));

        AiReturns(Order("Ahmed", "Dakao Kaju", 15));
        await engine.HandleIncomingMessageAsync(Phone, "Ahmed, 15kg dakao kaju, 03001234567", default);
        Assert.Contains(_sent, m => m.Contains("Rate: Rs.300/kg (10kg+ tier)") && m.Contains("Total: Rs.4,500"));

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(4500m, (await db.Orders.FirstAsync()).Total);
    }

    [Fact]
    public async Task NewOrder_DefaultsToCod()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        Assert.Contains(_sent, m => m.Contains("Payment: COD (delivery par cash)"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(OrderPaymentMethod.Cod, (await db.Orders.FirstAsync()).PaymentMethod);
    }

    [Fact]
    public async Task MultipleCustomersInOneMessage_ConfirmAllWithOneYes()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var analysis = Order("Ayesha", "Lawn Suit", 2);
        analysis.AdditionalOrders.Add(new AiOrderDraft
        {
            CustomerName = "Bilal", Phone = "03009876543",
            Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } }
        });
        AiReturns(analysis);

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha 2 suit, Bilal 1 kurti, 03001234567 aur 03009876543", default);
        Assert.Contains(_sent, m => m.Contains("Mujhe 2 alag orders mile"));

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(2, await db.Orders.CountAsync());
        Assert.Contains(_sent, m => m.Contains("2 orders saved"));
    }

    [Fact]
    public async Task OffTopic_GetsGracefulRedirect_AndFeedbackIsLoggedAgainstOrder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        AiReturns(new AiMessageAnalysis { Intent = "off_topic", ClarificationQuestion = "Rest karein!" });
        await engine.HandleIncomingMessageAsync(Phone, "bohat thak gayi hoon aaj", default);
        Assert.Contains(_sent, m => m.Contains("Rest karein!") && m.Contains("menu"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);

        AiReturns(Order("Ayesha", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        AiReturns(new AiMessageAnalysis
        {
            Intent = "customer_feedback",
            Feedback = new AiCustomerFeedback { CustomerName = "ayesha", Text = "bahut khush thi", Sentiment = "positive" }
        });
        await engine.HandleIncomingMessageAsync(Phone, "ayesha bahut khush thi order se", default);

        var feedback = await db.CustomerFeedbacks.FirstAsync();
        Assert.NotNull(feedback.OrderId);
        Assert.Contains(_sent, m => m.Contains($"feedback saved against Order #{feedback.OrderId} (Ayesha)"));
    }

    [Fact]
    public async Task ReceiptScreenshot_MatchesUnpaidOrder_AndMarksPaid()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.FirstAsync();

        _media.Setup(m => m.DownloadAsync("media-1", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "image/jpeg"));
        _ai.Setup(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", Receipt = new AiPaymentReceipt { Amount = 1800, Provider = "JazzCash", TransactionId = "JC9928817" } });

        await engine.HandleImageMessageAsync(Phone, "media-1", null, default);
        Assert.Contains(_buttons, b => b.Body.Contains("Payment receipt mila — Rs.1,800") && b.Buttons.Contains($"Order #{order.Id} - Sara"));

        await engine.HandleIncomingMessageAsync(Phone, $"Order #{order.Id} - Sara", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.NotNull(order.PaidAt);
        Assert.Contains("JC9928817", order.Notes);
    }

    [Fact]
    public async Task ScreenshotOrder_GoesThroughTheNormalConfirmStep()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        _media.Setup(m => m.DownloadAsync("media-2", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "image/jpeg"));
        var analysis = Order("Ramsha", "Lawn Suit", 2);
        _ai.Setup(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

        await engine.HandleImageMessageAsync(Phone, "media-2", null, default);

        Assert.Contains(_sent, m => m.Contains("Screenshot se order parh liya"));
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task DeleteCustomer_HidesFromList_AndRestoreBringsBack()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Bilal", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        await engine.HandleIncomingMessageAsync(Phone, "delete customer bilal", default);
        Assert.Contains(_sent, m => m.Contains("Bilal (1 orders") && m.Contains("30 din tak restore"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.NotNull((await db.Customers.FirstAsync()).DeletedAt);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "customer list", default);
        Assert.Contains(_sent, m => m.Contains("Abhi koi customer nahi hai"));

        await engine.HandleIncomingMessageAsync(Phone, "restore customer bilal", default);
        Assert.Null((await db.Customers.FirstAsync()).DeletedAt);
        Assert.Contains(_sent, m => m.Contains("Bilal wapas active ho gaya"));
    }

    [Fact]
    public async Task LoyaltyThreshold_OffersDiscount_AndYesAppliesIt()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "create loyalty: 2 orders = 10 percent off", default);
        AiReturns(Order("Ayesha", "Lawn Suit", 2));

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha, 2 suit, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        AiReturns(Order("Ayesha", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "1", default); // not a duplicate (different product), but answer defensively
        if ((await db.Sessions.FirstAsync()).State == ConversationState.AwaitingOrderConfirmation)
            await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Contains(_sent, m => m.Contains("2nd order hai") && m.Contains("10% loyalty discount"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var second = await db.Orders.OrderByDescending(o => o.Id).FirstAsync();
        Assert.Equal(1620m, second.Total);
        Assert.Contains(_sent, m => m.Contains("Loyalty discount applied") && m.Contains("Rs.180 off"));
    }

    [Fact]
    public async Task Undo_AfterNewOrder_CancelsIt()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);

        Assert.Equal(OrderStatus.Cancelled, (await db.Orders.FirstAsync()).Status);
    }

    [Fact]
    public async Task Broadcast_AsksChannelThenAudience_AndCampaignStatusReports()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        _sender.Setup(s => s.SendTemplateMessageAsync("923001234567", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await engine.HandleIncomingMessageAsync(Phone, "naya stock aaya — sab customers ko batao: naye lawn suits available", default);
        Assert.Contains(_buttons, b => b.Body.Contains("Konse channel"));
        await engine.HandleIncomingMessageAsync(Phone, "WhatsApp", default);
        await engine.HandleIncomingMessageAsync(Phone, "1", default);
        Assert.Contains(_sent, m => m.Contains("1 customers ko bheja gaya") && m.Contains("WhatsApp: 1 sent"));

        await engine.HandleIncomingMessageAsync(Phone, "campaign status", default);
        Assert.Contains(_sent, m => m.Contains("Last Campaign") && m.Contains("Bheja gaya: 1 customers ko"));
        Assert.Equal(1, await db.CampaignSends.CountAsync());
    }

    [Fact]
    public async Task ScheduledJobs_SendWeeklySummaryOnce_AndTrialReminder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var seller = await db.Sellers.FirstAsync();
        seller.CreatedAt = DateTime.UtcNow.AddDays(-20);
        seller.TrialEndsAt = DateTime.UtcNow.AddDays(1.5);
        await db.SaveChangesAsync();

        await engine.RunScheduledJobsAsync(DateTime.UtcNow);
        await engine.RunScheduledJobsAsync(DateTime.UtcNow);

        Assert.Single(_sent, m => m.Contains("Weekly Summary — Ayesha Collections"));
        Assert.Single(_buttons, b => b.Body.Contains("free trial") && b.Body.Contains("khatam ho raha hai"));
    }

    [Fact]
    public async Task MessageLog_RecordsInboundAndOutbound()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);

        Assert.True(await db.MessageLogs.AnyAsync(m => m.Direction == "inbound" && m.RawText == "catalog"));
        Assert.True(await db.MessageLogs.AnyAsync(m => m.Direction == "outbound" && m.RawText.Contains("Aapka Catalog")));
    }

    [Theory]
    [InlineData("Sugar 5 kg - 500", "Sugar", "kg", 5)]
    [InlineData("Rice 10kg - 1200", "Rice", "kg", 10)]
    [InlineData("Lawn Suit - 3500", "Lawn Suit", "piece", 1)]
    public void ProductLine_SplitsUnitFromName(string line, string name, string unit, decimal qty)
    {
        Assert.True(CommandParser.TryParseProductLine(line, out ProductLine? product));
        Assert.Equal(name, product!.Name);
        Assert.Equal(unit, product.UnitType);
        Assert.Equal(qty, product.UnitQty);
    }

    [Fact]
    public void SchemaPatcher_CreatesTablesAddedAfterFirstDeploy()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE \"Products\" (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        SqliteSchemaPatcher.Apply(db);

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('PriceTiers','Campaigns','CampaignSends','MessageLogs')";
        Assert.Equal(4L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Products') WHERE name IN ('UnitType','UnitQty')";
        Assert.Equal(2L, check.ExecuteScalar());
    }
}
