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

public class ExpenseParserTests
{
    [Theory]
    [InlineData("expense 500 packaging", 500, "packaging")]
    [InlineData("Expense 1,200 shop rent", 1200, "shop rent")]
    [InlineData("expense: Rs 350.50 courier bag", 350.50, "courier bag")]
    [InlineData("kharcha 800 petrol", 800, "petrol")]
    [InlineData("expense packaging 500", 500, "packaging")]
    [InlineData("expense 500", 500, "")]
    [InlineData("خرچہ 700 کرایہ", 700, "کرایہ")]
    public void ParsesExpense(string message, double amount, string note)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.Expense, parsed!.Kind);
        Assert.Equal((decimal)amount, parsed.Amount);
        Assert.Equal(note, parsed.Text);
    }

    [Theory]
    [InlineData("expenses", "month")]
    [InlineData("expenses today", "today")]
    [InlineData("expenses this month", "month")]
    [InlineData("expenses last month", "lastmonth")]
    [InlineData("kharche", "month")]
    public void ParsesExpenseList(string message, string period)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.ExpenseList, parsed!.Kind);
        Assert.Equal(period, parsed.Text);
    }

    [Theory]
    [InlineData("monthly net", "month")]
    [InlineData("Monthly Net", "month")]
    [InlineData("net", "month")]
    [InlineData("is mahine ka net", "month")]
    [InlineData("last month net", "lastmonth")]
    [InlineData("monthly net last month", "lastmonth")]
    public void ParsesMonthlyNet(string message, string period)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.MonthlyNet, parsed!.Kind);
        Assert.Equal(period, parsed.Text);
    }

    [Theory]
    [InlineData("expense")]
    [InlineData("expense packaging")]
    [InlineData("expensive kurti 500")]
    public void NotAnExpense(string message)
    {
        var kind = CommandParser.TryParse(message)?.Kind;
        Assert.NotEqual(CommandKind.Expense, kind);
    }
}

public class ExpenseEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public ExpenseEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task<Seller> OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
        return await db.Sellers.FirstAsync();
    }

    private static async Task AddOrderAsync(AppDbContext db, Seller seller, decimal total, OrderStatus status, DateTime? createdAt = null)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.SellerId == seller.Id)
                       ?? db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" }).Entity;
        await db.SaveChangesAsync();
        var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Status = status, Subtotal = total, Total = total };
        if (createdAt is { } at) order.CreatedAt = at;
        order.Items.Add(new OrderItem { ProductNameSnapshot = "Suit", UnitPrice = total, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task LogsExpense_WithFirstWordAsCategory_AndShowsMonthTotal()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "expense 500 Packaging boxes", default);
        await engine.HandleIncomingMessageAsync(Phone, "expense 250 packaging tape", default);

        var saved = await db.Expenses.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(new[] { 500m, 250m }, saved.Select(x => x.Amount));
        Assert.All(saved, x => Assert.Equal("packaging", x.Category));
        Assert.Equal("Packaging boxes", saved[0].Note);
        Assert.Contains(_sent, m => m.Contains("Rs.500") && m.Contains("Packaging boxes"));
        Assert.Contains(_sent, m => m.Contains("Is maah ka total kharcha: Rs.750"));
    }

    [Fact]
    public async Task ZeroExpense_IsRejected()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "expense 0 packaging", default);

        Assert.Empty(db.Expenses);
        Assert.Contains(_sent, m => m.Contains("0 se zyada"));
    }

    [Fact]
    public async Task Undo_RemovesTheLastExpense()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "expense 500 packaging", default);
        await engine.HandleIncomingMessageAsync(Phone, "undo", default);

        Assert.Empty(db.Expenses);
        Assert.Contains(_sent, m => m.Contains("Reverted") && m.Contains("Rs.500"));
    }

    [Fact]
    public async Task ExpenseList_ShowsThisMonthOnly_WithTotal()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await OnboardAsync(db);
        db.Expenses.Add(new Expense { SellerId = seller.Id, Amount = 4000, Category = "rent", Note = "rent", CreatedAt = DateTime.UtcNow.AddMonths(-2) });
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "expense 500 packaging", default);
        await engine.HandleIncomingMessageAsync(Phone, "expense 300 petrol", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "expenses", default);

        Assert.Contains(_sent, m => m.Contains("packaging") && m.Contains("petrol") && !m.Contains("rent") && m.Contains("Total: Rs.800"));
    }

    [Fact]
    public async Task EmptyExpenseList_ExplainsHowToStart()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "expenses", default);

        Assert.Contains(_sent, m => m.Contains("koi kharcha record nahi") && m.Contains("expense 500 packaging"));
    }

    [Fact]
    public async Task MonthlyNet_IsSalesMinusExpenses_ExcludingCancelledReturnedAndOtherMonths()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await OnboardAsync(db);
        await AddOrderAsync(db, seller, 3000, OrderStatus.Pending);
        await AddOrderAsync(db, seller, 2000, OrderStatus.Delivered);
        await AddOrderAsync(db, seller, 9000, OrderStatus.Cancelled);
        await AddOrderAsync(db, seller, 8000, OrderStatus.Returned);
        await AddOrderAsync(db, seller, 7000, OrderStatus.Delivered, DateTime.UtcNow.AddMonths(-2));
        db.Expenses.Add(new Expense { SellerId = seller.Id, Amount = 6000, Category = "rent", CreatedAt = DateTime.UtcNow.AddMonths(-2) });
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "expense 700 packaging", default);
        await engine.HandleIncomingMessageAsync(Phone, "expense 300 packaging", default);
        await engine.HandleIncomingMessageAsync(Phone, "expense 500 petrol", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "monthly net", default);

        Assert.Contains(_sent, m => m.Contains("Sales: Rs.5,000 (2 orders)") && m.Contains("Kharcha: Rs.1,500 (3)")
            && m.Contains("Net: Rs.3,500") && m.Contains("• packaging: Rs.1,000") && m.Contains("• petrol: Rs.500") && !m.Contains("rent"));
    }

    [Fact]
    public async Task MonthlyNet_GoesNegative_WhenExpensesExceedSales()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await OnboardAsync(db);
        await AddOrderAsync(db, seller, 1000, OrderStatus.Pending);
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "expense 1500 rent", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "monthly net", default);

        Assert.Contains(_sent, m => m.Contains("🔻") && m.Contains("Net: Rs.-500"));
    }

    [Fact]
    public async Task MonthlyNet_LastMonth_UsesThePreviousCalendarMonth()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await OnboardAsync(db);
        var midLastMonth = DateTime.UtcNow.AddDays(-DateTime.UtcNow.Day).AddDays(-10);
        await AddOrderAsync(db, seller, 4000, OrderStatus.Delivered, midLastMonth);
        db.Expenses.Add(new Expense { SellerId = seller.Id, Amount = 1000, Category = "rent", CreatedAt = midLastMonth });
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "last month net", default);

        Assert.Contains(_sent, m => m.Contains("pichla maah") && m.Contains("Sales: Rs.4,000") && m.Contains("Net: Rs.3,000"));
    }

    [Fact]
    public async Task ExpensesOfOtherSellers_AreNotCounted()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await OnboardAsync(db);
        db.Expenses.Add(new Expense { SellerId = seller.Id + 99, Amount = 5000, Category = "rent" });
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "monthly net", default);

        Assert.Contains(_sent, m => m.Contains("Kharcha: Rs.0 (0)"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
