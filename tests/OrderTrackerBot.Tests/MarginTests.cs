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

public class MarginParserTests
{
    [Theory]
    [InlineData("margin")]
    [InlineData("margins")]
    [InlineData("margin list")]
    [InlineData("margin report")]
    [InlineData("profit margin")]
    [InlineData("discount room")]
    [InlineData("discount limit")]
    public void MarginPhrases_AreTheMarginReport(string text) => Assert.Equal(CommandKind.Margin, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("profit", CommandKind.Profit)]
    [InlineData("profit today", CommandKind.Profit)]
    [InlineData("munafa", CommandKind.Profit)]
    [InlineData("discount list", CommandKind.DiscountList)]
    [InlineData("discount performance", CommandKind.DiscountPerformance)]
    [InlineData("create discount: EID10, 10 percent", CommandKind.CreateDiscount)]
    public void NeighbouringCommands_AreUnchanged(string text, CommandKind expected) => Assert.Equal(expected, CommandParser.TryParse(text)?.Kind);
}

public class MarginEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();
    private Seller _seller = null!;
    private Customer _customer = null!;

    public MarginEngineTests() =>
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

    private async Task<Product> ProductAsync(AppDbContext db, string name, decimal price, decimal? cost, bool active = true)
    {
        var p = new Product { SellerId = _seller.Id, Name = name, Price = price, CostPrice = cost, IsActive = active };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private async Task SoldAsync(AppDbContext db, Product p, int qty, int daysAgo = 1, OrderStatus status = OrderStatus.Delivered)
    {
        var order = new Order { SellerId = _seller.Id, CustomerId = _customer.Id, Status = status, CreatedAt = DateTime.UtcNow.AddDays(-daysAgo) };
        order.Items.Add(new OrderItem { ProductId = p.Id, ProductNameSnapshot = p.Name, UnitPrice = p.Price, Quantity = qty });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ShowsMargin_SuggestedHalf_AndBreakEven_BestMarginFirst()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var kurti = await ProductAsync(db, "Kurti", 1800, 1000);      // margin 44.4%
        var lawn = await ProductAsync(db, "Lawn Suit", 3500, 3000);   // margin 14.3%
        await SoldAsync(db, kurti, 3);
        await SoldAsync(db, kurti, 2, daysAgo: 3);

        await engine.HandleIncomingMessageAsync(Phone, "margin", default);

        var reply = Assert.Single(_text);
        Assert.Contains("1. Kurti — Rs.1,800, cost Rs.1,000", reply);
        Assert.Contains("margin 44% · munasib discount ~22% · is se zyada nahi 44% · 30 din: 5 bike", reply);
        Assert.Contains("2. Lawn Suit — Rs.3,500, cost Rs.3,000", reply);
        Assert.Contains("margin 14% · munasib discount ~7% · is se zyada nahi 14% · 30 din: 0 bike", reply);
        Assert.True(reply.IndexOf("1. Kurti", StringComparison.Ordinal) < reply.IndexOf("2. Lawn Suit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelledReturnedAndOldOrders_DoNotCountAsSold()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var kurti = await ProductAsync(db, "Kurti", 1800, 1000);
        await SoldAsync(db, kurti, 1);
        await SoldAsync(db, kurti, 7, status: OrderStatus.Cancelled);
        await SoldAsync(db, kurti, 7, status: OrderStatus.Returned);
        await SoldAsync(db, kurti, 7, daysAgo: 45);

        await engine.HandleIncomingMessageAsync(Phone, "margin", default);

        Assert.Contains("30 din: 1 bike", Assert.Single(_text));
    }

    [Fact]
    public async Task NoMarginProduct_IsFlagged_AndMissingCostsAreCounted_NotGuessed()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await ProductAsync(db, "Kurti", 1800, 1000);
        await ProductAsync(db, "Sample", 500, 500);       // sells at cost
        await ProductAsync(db, "Dupatta", 600, null);     // no cost saved
        await ProductAsync(db, "Old Shawl", 900, 100, active: false); // switched off

        await engine.HandleIncomingMessageAsync(Phone, "margin", default);

        var reply = Assert.Single(_text);
        Assert.Contains("Sample", reply);
        Assert.Contains("margin nahi", reply);
        Assert.Contains("1 product(s) ki cost save nahi", reply);
        Assert.DoesNotContain("Dupatta —", reply);
        Assert.DoesNotContain("Old Shawl", reply);
    }

    [Fact]
    public async Task NoProducts_AndNoCosts_SayWhatToDo()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await engine.HandleIncomingMessageAsync(Phone, "margin", default);
        Assert.Contains("koi product nahi", _text.Last());

        await ProductAsync(db, "Kurti", 1800, null);
        await engine.HandleIncomingMessageAsync(Phone, "margin", default);
        Assert.Contains("cost save nahi", _text.Last());
        Assert.Contains("Water Bottle - 500, cost 350", _text.Last());
    }

    [Fact]
    public async Task OnlyTheTopTenAreListed_WithACountOfTheRest()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        for (var i = 1; i <= 13; i++) await ProductAsync(db, $"Item{i:00}", 1000, 1000 - i * 10);

        await engine.HandleIncomingMessageAsync(Phone, "margin", default);

        var reply = Assert.Single(_text);
        Assert.Contains("10. ", reply);
        Assert.DoesNotContain("11. ", reply);
        Assert.Contains("aur 3 products", reply);
        Assert.True(reply.Length < 4096);
    }

    [Fact]
    public async Task OtherSellersProducts_AreNeverShown()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await ProductAsync(db, "Kurti", 1800, 1000);
        var other = new Seller { WhatsAppPhoneNumber = "923009999999", BusinessName = "Other", OnboardingComplete = true };
        db.Sellers.Add(other);
        await db.SaveChangesAsync();
        db.Products.Add(new Product { SellerId = other.Id, Name = "Secret Item", Price = 5000, CostPrice = 100 });
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "margin", default);

        Assert.DoesNotContain("Secret Item", Assert.Single(_text));
    }

    public void Dispose() => _dbFactory.Dispose();
}
