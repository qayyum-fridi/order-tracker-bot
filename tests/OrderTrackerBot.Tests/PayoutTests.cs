using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class PayoutParserTests
{
    [Theory]
    [InlineData("payout 11700 TCS orders 1 2 3 fee 300", "record")]
    [InlineData("payout Rs. 11,700 TCS", "record")]
    [InlineData("settlement 5000 JazzCash", "record")]
    [InlineData("payout", "pending")]
    [InlineData("payouts", "pending")]
    [InlineData("payout pending", "pending")]
    [InlineData("awaiting payout", "pending")]
    [InlineData("payout list", "list")]
    [InlineData("payout history", "list")]
    [InlineData("payout skip 12 13", "skip")]
    [InlineData("payout undo", "undo")]
    public void Payout_IsRecognised(string text, string action)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.Payout, parsed.Kind);
        Assert.Equal(action, parsed.Text);
    }

    [Fact]
    public void Record_CarriesTheAmountAndTheRest()
    {
        var parsed = CommandParser.TryParse("payout Rs. 11,700 TCS orders 1 2 3 fee 300")!;
        Assert.Equal(11700m, parsed.Amount);
        Assert.Equal("TCS orders 1 2 3 fee 300", parsed.Text2);
        Assert.Equal("12 13", CommandParser.TryParse("payout skip 12 13")!.Text2);
    }

    [Theory]
    [InlineData("payout kab aayega mera")]
    [InlineData("payout ka kuch pata nahi")]
    public void AFreeSentenceAboutPayouts_IsNotACommand(string text) =>
        Assert.NotEqual(CommandKind.Payout, CommandParser.TryParse(text)?.Kind);

    [Fact]
    public void Args_SourceOrdersFeeAndReference()
    {
        var a = ConversationEngine.ParsePayoutArgs("TCS orders 1 2 3 fee 300 ref AB12");
        Assert.Equal("TCS", a.Source);
        Assert.Equal(new[] { 1, 2, 3 }, a.Orders);
        Assert.Equal(300m, a.Fee);
        Assert.Equal("AB12", a.Reference);

        var b = ConversationEngine.ParsePayoutArgs("Leopards se mila order #4, #5 charges Rs. 1,250");
        Assert.Equal("Leopards", b.Source);
        Assert.Equal(new[] { 4, 5 }, b.Orders);
        Assert.Equal(1250m, b.Fee);

        var c = ConversationEngine.ParsePayoutArgs("");
        Assert.Equal("Courier", c.Source);
        Assert.Empty(c.Orders);
        Assert.Equal(0m, c.Fee);
    }

    [Fact]
    public void PayoutKeywordArea_OptionsAreRunnable()
    {
        Assert.True(ConversationEngine.TryKeywordClarification("mera payout kab aayega", out _, out var options));
        Assert.All(options, o => Assert.NotNull(CommandParser.TryParse(o)));
    }
}

public class PayoutEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();

    public PayoutEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private ConversationEngine Engine(AppDbContext db) => new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>());

    /// <summary>A seller with three delivered, unpaid COD orders of Rs.3,500 shipped with TCS.</summary>
    private async Task<(Seller Seller, List<Order> Orders)> SeedAsync(AppDbContext db, int orders = 3, string courier = "TCS")
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var customer = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var list = new List<Order>();
        for (var i = 0; i < orders; i++)
        {
            var o = new Order
            {
                SellerId = seller.Id, CustomerId = customer.Id, Subtotal = 3500, Total = 3500, PaymentMethod = OrderPaymentMethod.Cod,
                Status = OrderStatus.Delivered, DeliveredAt = DateTime.UtcNow.AddDays(-3), TrackingCourier = courier
            };
            o.Items.Add(new OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 1 });
            db.Orders.Add(o);
            list.Add(o);
        }
        await db.SaveChangesAsync();
        return (seller, list);
    }

    [Fact]
    public async Task Pending_ListsDeliveredCodOrders_ByCourier_WithTotals()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "payout pending", default);

        Assert.Contains(_sent, m => m.Contains("TCS: 3 orders") && m.Contains("10,500") && m.Contains("3 din se"));
    }

    [Fact]
    public async Task Pending_IgnoresPaidShippedOrPrepaidOrders()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db);
        orders[0].PaymentStatus = PaymentStatus.Paid;     // cash already collected
        orders[1].Status = OrderStatus.Shipped;           // not delivered yet
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, "payouts", default);

        Assert.Contains(_sent, m => m.Contains("TCS: 1 orders") && m.Contains("3,500"));
    }

    [Fact]
    public async Task Record_WithOrders_SettlesThemMarksPaid_AndChecksTheAmount()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db);
        var ids = string.Join(" ", orders.Select(o => o.Id));

        await Engine(db).HandleIncomingMessageAsync(Phone, $"payout 10200 TCS orders {ids} fee 300", default);

        Assert.Contains(_sent, m => m.Contains("Payout #1") && m.Contains("Barabar") && m.Contains("10,500"));
        var saved = await db.Orders.AsNoTracking().ToListAsync();
        Assert.All(saved, o =>
        {
            Assert.Equal(1, o.PayoutId);
            Assert.NotNull(o.PayoutSettledAt);
            Assert.Equal(PaymentStatus.Paid, o.PaymentStatus);
            Assert.Equal(3500m, o.AmountPaid);
        });
        var payout = await db.Payouts.AsNoTracking().SingleAsync();
        Assert.Equal(10200m, payout.Amount);
        Assert.Equal(300m, payout.Fee);
        Assert.Equal("TCS", payout.Source);

        _sent.Clear();
        await Engine(db).HandleIncomingMessageAsync(Phone, "payout pending", default);
        Assert.Contains(_sent, m => m.Contains("Koi payout baqi nahi"));
    }

    [Fact]
    public async Task ShortPayout_IsFlagged_WithTheMissingAmount()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, $"payout 9000 TCS orders {orders[0].Id} {orders[1].Id} {orders[2].Id}", default);

        Assert.Contains(_sent, m => m.Contains("Rs.1,500 KAM aaya"));
    }

    [Fact]
    public async Task TaxWithheldAndAdvance_AreInsideWhatWasExpected()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db, orders: 1);
        orders[0].TaxWithheld = 100;
        orders[0].AmountPaid = 500;     // an advance the customer already paid
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, $"payout 2900 TCS orders {orders[0].Id}", default);

        Assert.Contains(_sent, m => m.Contains("Barabar"));   // 3500 - 100 - 500
    }

    [Fact]
    public async Task Undo_PutsOrdersBackExactlyAsTheyWere()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db);
        orders[0].AmountPaid = 500;
        await db.SaveChangesAsync();
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, $"payout 10000 TCS orders {string.Join(" ", orders.Select(o => o.Id))}", default);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "payout undo", default);

        Assert.Contains(_sent, m => m.Contains("hata diya") && m.Contains("3 orders"));
        Assert.Empty(await db.Payouts.ToListAsync());
        var back = await db.Orders.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
        Assert.All(back, o =>
        {
            Assert.Null(o.PayoutId);
            Assert.Null(o.PayoutSettledAt);
            Assert.Equal(PaymentStatus.Unpaid, o.PaymentStatus);
        });
        Assert.Equal(500m, back[0].AmountPaid);
        Assert.Equal(0m, back[1].AmountPaid);
    }

    [Fact]
    public async Task Record_WithoutOrders_SuggestsTheMatchingPendingOnes()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "payout 10500 TCS", default);

        Assert.Contains(_sent, m => m.Contains("baqi orders ke total se milta hai") && m.Contains($"orders {string.Join(" ", orders.Select(o => o.Id))}"));
        Assert.Single(await db.Payouts.ToListAsync());
        Assert.All(await db.Orders.AsNoTracking().ToListAsync(), o => Assert.Null(o.PayoutId));
    }

    [Fact]
    public async Task BadOrders_RefuseTheWholePayout()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db);
        orders[1].Status = OrderStatus.Cancelled;
        orders[2].PaymentMethod = OrderPaymentMethod.Manual;
        await db.SaveChangesAsync();
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"payout 3500 TCS orders {orders[0].Id} {orders[1].Id} {orders[2].Id}", default);
        Assert.Contains(_sent, m => m.Contains("kuch save nahi kiya") && m.Contains("CANCELLED") && m.Contains("COD order nahi"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "payout 3500 TCS orders 999", default);
        Assert.Contains(_sent, m => m.Contains("#999 nahi mila"));
        Assert.Empty(await db.Payouts.ToListAsync());
    }

    [Fact]
    public async Task AnOrderAlreadySettled_CannotBeSettledTwice()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db, orders: 1);
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, $"payout 3500 TCS orders {orders[0].Id}", default);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, $"payout 3500 TCS orders {orders[0].Id}", default);

        Assert.Contains(_sent, m => m.Contains("pehle se settle hai") && m.Contains("payout #1"));
        Assert.Single(await db.Payouts.ToListAsync());
    }

    [Fact]
    public async Task AnotherSellersOrders_AreNotTouched()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db, orders: 1);
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = "923009999999", BusinessName = "Other", OnboardingComplete = true, Session = new ConversationSession() });
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync("923009999999", $"payout 3500 TCS orders {orders[0].Id}", default);

        Assert.Empty(await db.Payouts.ToListAsync());
        Assert.Null((await db.Orders.AsNoTracking().FirstAsync()).PayoutId);
    }

    [Fact]
    public async Task Skip_RemovesOrdersFromPending_WithoutAPayout()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"payout skip {orders[0].Id} {orders[1].Id}", default);
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "payout pending", default);

        Assert.Contains(_sent, m => m.Contains("TCS: 1 orders"));
        Assert.Empty(await db.Payouts.ToListAsync());
    }

    [Fact]
    public async Task List_ShowsRecentPayoutsWithOrderCounts()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db, orders: 2);
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, $"payout 7000 TCS orders {orders[0].Id} {orders[1].Id}", default);
        await engine.HandleIncomingMessageAsync(Phone, "payout 500 JazzCash", default);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "payout list", default);

        Assert.Contains(_sent, m => m.Contains("#1") && m.Contains("TCS") && m.Contains("2 orders") && m.Contains("#2") && m.Contains("JazzCash") && m.Contains("orders jore nahi"));
    }

    [Fact]
    public async Task CourierSettledCod_IsNotDrawerCash()
    {
        using var db = _dbFactory.CreateContext();
        var (_, orders) = await SeedAsync(db, orders: 1);
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "drawer open 1000", default);
        await engine.HandleIncomingMessageAsync(Phone, $"payout 3500 TCS orders {orders[0].Id}", default);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "drawer", default);

        Assert.Contains(_sent, m => m.Contains("Ab hona chahiye: Rs.1,000"));
    }

    [Fact]
    public async Task ResetAccount_DeletesPayouts()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, orders) = await SeedAsync(db, orders: 1);
        await Engine(db).HandleIncomingMessageAsync(Phone, $"payout 3500 TCS orders {orders[0].Id}", default);

        await SellerAccountReset.ResetAsync(db, seller, default);
        await db.SaveChangesAsync();

        Assert.Empty(await db.Payouts.ToListAsync());
    }

    public void Dispose() => _dbFactory.Dispose();
}
