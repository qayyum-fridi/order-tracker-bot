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

public class DayAccountParserTests
{
    [Theory]
    [InlineData("aaj ka hisaab")]
    [InlineData("Aaj ka hisab")]
    [InlineData("آج کا حساب")]
    [InlineData("hisaab kitab")]
    public void AccountPhrases_AreTodaysSummary(string text) => Assert.Equal(CommandKind.TodaysSummary, CommandParser.TryParse(text)?.Kind);
}

public class DayAccountTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();
    private readonly List<string[]> _bars = new();
    private Seller _seller = null!;
    private Customer _customer = null!;

    public DayAccountTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, l, _) => _bars.Add(l.ToArray())).Returns(Task.CompletedTask);
    }

    private async Task<(ConversationEngine Engine, AppDbContext Db)> SetupAsync(bool bar = false)
    {
        var db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(_seller);
        await db.SaveChangesAsync();
        _customer = new Customer { SellerId = _seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(_customer);
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(), _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = bar, ConfirmButtons = false, SuggestionButtons = false });
        return (engine, db);
    }

    private async Task OrderAsync(AppDbContext db, OrderStatus status, params (string Name, int Qty, decimal Price)[] items)
    {
        var order = new Order { SellerId = _seller.Id, CustomerId = _customer.Id, Status = status, CreatedAt = DateTime.UtcNow };
        foreach (var (name, qty, price) in items)
            order.Items.Add(new OrderItem { ProductNameSnapshot = name, Quantity = qty, UnitPrice = price });
        order.Subtotal = order.Total = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Today_ShowsTopItem_Expenses_AndDrawerCash()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, ("Lawn Suit", 2, 3500m), ("Dupatta", 1, 600m));
        await OrderAsync(db, OrderStatus.Pending, ("Kurti", 1, 1800m));
        await OrderAsync(db, OrderStatus.Cancelled, ("Shawl", 9, 100m));      // cancelled: not counted
        db.Expenses.Add(new Expense { SellerId = _seller.Id, Amount = 1500, Category = "akram", Note = "akram petrol", CreatedAt = DateTime.UtcNow });
        db.CashDrawerSessions.Add(new CashDrawerSession { SellerId = _seller.Id, OpenedAt = DateTime.UtcNow.AddHours(-1), OpeningAmount = 5000 });
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "aaj ka hisaab", default);

        var reply = Assert.Single(_text);
        Assert.Contains("Orders: 2", reply);
        Assert.Contains("Total sales today: Rs.9,400", reply);
        Assert.Contains("Sab se zyada bika: Lawn Suit (2 bike)", reply);
        Assert.DoesNotContain("Shawl", reply);
        Assert.Contains("Kharcha: Rs.1,500 (akram petrol)", reply);
        Assert.Contains("Drawer mein hona chahiye: Rs.5,000", reply);
    }

    [Fact]
    public async Task NoDrawerNoExpensesNoOrders_SaysSo_AndOffersExpensesButton()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;

        await engine.HandleIncomingMessageAsync(Phone, "aaj ka hisaab", default);

        var reply = Assert.Single(_text);
        Assert.Contains("Orders: 0", reply);
        Assert.DoesNotContain("Sab se zyada bika", reply);
        Assert.Contains("Kharcha: koi nahi", reply);
        Assert.Contains("Drawer band hai", reply);
    }

    [Fact]
    public async Task FollowUpBar_OffersOrdersAndDrawer_WhenADrawerIsOpen_ElseExpenses()
    {
        var (engine, db) = await SetupAsync(bar: true);
        using var db2 = db;
        await engine.HandleIncomingMessageAsync(Phone, "aaj ka hisaab", default);
        Assert.Equal(new[] { "orders today", "expenses today" }, _bars.Last().Take(2));
        Assert.All(_bars.Last(), l => Assert.True(l.Length <= 20));

        db.CashDrawerSessions.Add(new CashDrawerSession { SellerId = _seller.Id, OpenedAt = DateTime.UtcNow.AddHours(-1), OpeningAmount = 1000 });
        await db.SaveChangesAsync();
        _bars.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "aaj ka hisaab", default);
        Assert.Equal(new[] { "orders today", "drawer" }, _bars.Last().Take(2));
    }

    [Fact]
    public async Task Yesterday_KeepsThePlainSummary()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, ("Lawn Suit", 2, 3500m));

        await engine.HandleIncomingMessageAsync(Phone, "kal ka hisaab", default);

        var reply = Assert.Single(_text);
        Assert.DoesNotContain("Sab se zyada bika", reply);
        Assert.DoesNotContain("Drawer", reply);
    }

    [Fact]
    public async Task TopItem_TiesGoToTheNameFirstAlphabetically_AndSumsAcrossOrders()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, ("Kurti", 1, 1800m), ("Dupatta", 2, 600m));
        await OrderAsync(db, OrderStatus.Delivered, ("Kurti", 1, 1800m));

        await engine.HandleIncomingMessageAsync(Phone, "aaj ka hisaab", default);

        Assert.Contains("Dupatta (2 bike)", Assert.Single(_text));
    }

    public void Dispose() => _dbFactory.Dispose();
}
