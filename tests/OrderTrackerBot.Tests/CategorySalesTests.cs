using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class CategorySalesParserTests
{
    [Theory]
    [InlineData("sales by category")]
    [InlineData("category wise sales")]
    [InlineData("category-wise sale")]
    [InlineData("category sales")]
    [InlineData("category ki sale")]
    public void NoPeriod_IsTheDefaultWindow(string text)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.CategorySales, parsed.Kind);
        Assert.Null(parsed.Text);
    }

    [Theory]
    [InlineData("sales by category this week")]
    [InlineData("category wise sales this week")]
    [InlineData("category sales last month")]
    public void WithAPeriod_CarriesItsKey(string text)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.CategorySales, parsed.Kind);
        Assert.False(string.IsNullOrEmpty(parsed.Text));
    }

    [Fact]
    public void NotAPeriod_IsNotTaken() => Assert.NotEqual(CommandKind.CategorySales, CommandParser.TryParse("sales by category purple monkey")?.Kind);
}

public class CategorySalesAndMonthTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();
    private Seller _seller = null!;
    private Customer _customer = null!;

    public CategorySalesAndMonthTests() =>
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);

    private async Task<(ConversationEngine Engine, AppDbContext Db)> SetupAsync()
    {
        var db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(_seller);
        await db.SaveChangesAsync();
        _customer = new Customer { SellerId = _seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(_customer);
        await db.SaveChangesAsync();
        return (new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(), _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false }), db);
    }

    private async Task<Product> ProductAsync(AppDbContext db, string name, decimal price, string? category)
    {
        var p = new Product { SellerId = _seller.Id, Name = name, Price = price, Category = category };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private async Task SoldAsync(AppDbContext db, Product? p, string name, decimal price, int qty, DateTime at, OrderStatus status = OrderStatus.Delivered)
    {
        var order = new Order { SellerId = _seller.Id, CustomerId = _customer.Id, Status = status, CreatedAt = at };
        order.Items.Add(new OrderItem { ProductId = p?.Id, ProductNameSnapshot = name, UnitPrice = price, Quantity = qty });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SalesAreSplitByCategory_BiggestFirst_UncategorisedLast_CancelledLeftOut()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var kurti = await ProductAsync(db, "Kurti", 1800, "Kapron");
        var polo = await ProductAsync(db, "Polo Shirt", 500, "kapron");      // same category, other spelling
        var kaju = await ProductAsync(db, "Kaju 1kg", 320, "Grocery");
        var misc = await ProductAsync(db, "Misc", 100, null);
        var now = DateTime.UtcNow.AddHours(-1);
        await SoldAsync(db, kurti, "Kurti", 1800, 2, now);                  // 3600
        await SoldAsync(db, polo, "Polo Shirt", 500, 2, now);               // 1000
        await SoldAsync(db, kaju, "Kaju 1kg", 320, 5, now);                 // 1600
        await SoldAsync(db, misc, "Misc", 100, 3, now);                     // 300
        await SoldAsync(db, null, "Old item", 700, 1, now);                 // no product at all: 700 uncategorised
        await SoldAsync(db, kurti, "Kurti", 1800, 9, now, OrderStatus.Cancelled);
        await SoldAsync(db, kurti, "Kurti", 1800, 9, now.AddDays(-60));     // outside the window

        await engine.HandleIncomingMessageAsync(Phone, "sales by category", default);

        var reply = Assert.Single(_text);
        Assert.Contains("1. Kapron — Rs.4,600 (4 items)", reply);
        Assert.Contains("2. Grocery — Rs.1,600 (5 items)", reply);
        Assert.Contains("3. Bina category — Rs.1,000 (4 items)", reply);
        Assert.Contains("Total: Rs.7,200", reply);
        Assert.Contains("category: general", reply); // how to categorise the rest
    }

    [Fact]
    public async Task NoOrdersInTheWindow_SaysSo()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;

        await engine.HandleIncomingMessageAsync(Phone, "category wise sales", default);

        Assert.Contains("koi order nahi", Assert.Single(_text));
    }

    [Fact]
    public async Task ThisWeekWindow_CountsOnlyThisWeek()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var kurti = await ProductAsync(db, "Kurti", 1800, "Kapron");
        await SoldAsync(db, kurti, "Kurti", 1800, 1, DateTime.UtcNow.AddHours(-1));
        await SoldAsync(db, kurti, "Kurti", 1800, 5, DateTime.UtcNow.AddDays(-20));

        await engine.HandleIncomingMessageAsync(Phone, "sales by category this week", default);

        var reply = Assert.Single(_text);
        Assert.Contains("Kapron — Rs.1,800 (1 items)", reply);
    }

    [Fact]
    public async Task ProductReport_ComparesThisMonthWithTheSameDaysOfLastMonth()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var polo = await ProductAsync(db, "Polo Shirt", 500, "Kapron");
        var lastMonth = SellerClock.PreviousMonthRangeUtc(_seller.TimeZoneId, DateTime.UtcNow);
        await SoldAsync(db, polo, "Polo Shirt", 500, 5, DateTime.UtcNow.AddMinutes(-5));         // this month: 5
        await SoldAsync(db, polo, "Polo Shirt", 500, 2, lastMonth.StartUtc.AddHours(1));        // last month, early: 2

        await engine.HandleIncomingMessageAsync(Phone, "Polo Shirt ka report", default);

        var reply = Assert.Single(_text);
        Assert.Contains("5 units, Rs.2,500", reply);
        Assert.Contains("2 units, Rs.1,000", reply);
        Assert.Contains("behtar ✅", reply);
        Assert.Contains("Pichla maah (poora): 2 units", reply);
    }

    [Fact]
    public async Task ProductReport_SaysKam_WhenThisMonthIsLower()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var polo = await ProductAsync(db, "Polo Shirt", 500, null);
        var lastMonth = SellerClock.PreviousMonthRangeUtc(_seller.TimeZoneId, DateTime.UtcNow);
        await SoldAsync(db, polo, "Polo Shirt", 500, 1, DateTime.UtcNow.AddMinutes(-5));
        await SoldAsync(db, polo, "Polo Shirt", 500, 4, lastMonth.StartUtc.AddHours(1));

        await engine.HandleIncomingMessageAsync(Phone, "Polo Shirt ka report", default);

        Assert.Contains("kam ⚠️", Assert.Single(_text));
    }

    public void Dispose() => _dbFactory.Dispose();
}
