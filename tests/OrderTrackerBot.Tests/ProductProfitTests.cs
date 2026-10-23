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

public class ProductProfitParserTests
{
    [Fact]
    public void ProfitPerProduct_IsTheProductWiseInsight()
    {
        var parsed = CommandParser.TryParse("profit per product");
        Assert.Equal(CommandKind.Insight, parsed?.Kind);
        Assert.Equal("productprofit", parsed?.Text);
    }

    [Theory]
    [InlineData("margin last month")]
    [InlineData("margin 7 days")]
    [InlineData("profit margin this week")]
    public void MarginForAPeriod_CarriesThePeriodKey(string text)
    {
        var parsed = CommandParser.TryParse(text);
        Assert.Equal(CommandKind.Margin, parsed?.Kind);
        Assert.NotNull(parsed?.Text2);
    }

    [Theory]
    [InlineData("margin")]
    [InlineData("margin list")]
    [InlineData("margin report")]
    public void PlainMargin_HasNoPeriod(string text)
    {
        var parsed = CommandParser.TryParse(text);
        Assert.Equal(CommandKind.Margin, parsed?.Kind);
        Assert.Null(parsed?.Text2);
    }
}

public class ProductProfitEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();
    private Seller _seller = null!;
    private Customer _customer = null!;

    public ProductProfitEngineTests() =>
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);

    public void Dispose() => _dbFactory.Dispose();

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

    private async Task<Product> ProductAsync(AppDbContext db, string name, decimal price, decimal? cost)
    {
        var p = new Product { SellerId = _seller.Id, Name = name, Price = price, CostPrice = cost, IsActive = true };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>A delivered order of <paramref name="qty"/> pieces at the product's price, costed at <paramref name="unitCost"/> when given.</summary>
    private async Task SoldAsync(AppDbContext db, Product p, int qty, DateTime when, decimal? unitCost)
    {
        var lineTotal = p.Price * qty;
        var order = new Order { SellerId = _seller.Id, CustomerId = _customer.Id, Status = OrderStatus.Delivered, CreatedAt = when, Subtotal = lineTotal, Total = lineTotal };
        order.Items.Add(new OrderItem { ProductId = p.Id, ProductNameSnapshot = p.Name, UnitPrice = p.Price, Quantity = qty, UnitCost = unitCost });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ProfitPerProduct_RanksProductsByProfit()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var kurti = await ProductAsync(db, "Kurti", 1800, 1000);      // 3 x 1800 = 5400 sales, 3000 cost, 2400 profit
        var lawn = await ProductAsync(db, "Lawn Suit", 3500, 3000);   // 1 x 3500 sales, 3000 cost, 500 profit
        await SoldAsync(db, kurti, 3, DateTime.UtcNow.AddHours(-2), 1000m);
        await SoldAsync(db, lawn, 1, DateTime.UtcNow.AddHours(-1), 3000m);

        await engine.HandleIncomingMessageAsync(Phone, "profit per product", default);

        var reply = Assert.Single(_text);
        Assert.Contains("1. Kurti — 3 piece · bikri Rs.5,400 · munafa Rs.2,400 · margin 44%", reply);
        Assert.Contains("2. Lawn Suit — 1 piece · bikri Rs.3,500 · munafa Rs.500 · margin 14%", reply);
        Assert.True(reply.IndexOf("1. Kurti", StringComparison.Ordinal) < reply.IndexOf("2. Lawn Suit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProductProfit_WithoutCost_SaysSoInsteadOfGuessing()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var kurti = await ProductAsync(db, "Kurti", 1800, null);
        await SoldAsync(db, kurti, 2, DateTime.UtcNow.AddHours(-2), null);

        await engine.HandleIncomingMessageAsync(Phone, "profit per product", default);

        Assert.Contains("cost price nahi likhi gayi", Assert.Single(_text));
    }

    [Fact]
    public async Task MarginLastMonth_UsesOnlyThatMonthsSales()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var kurti = await ProductAsync(db, "Kurti", 1800, 1000);
        var lastMonth = SellerClock.PreviousMonthRangeUtc(null, DateTime.UtcNow).StartUtc.AddDays(2);
        await SoldAsync(db, kurti, 2, lastMonth, 1000m);                    // 3600 sales, 2000 cost, 44% margin
        await SoldAsync(db, kurti, 5, DateTime.UtcNow.AddHours(-2), 1000m); // this month: must not count

        await engine.HandleIncomingMessageAsync(Phone, "margin last month", default);

        var reply = Assert.Single(_text);
        Assert.Contains("1. Kurti — 2 piece · bikri Rs.3,600 · munafa Rs.1,600 · margin 44%", reply);
    }
}
