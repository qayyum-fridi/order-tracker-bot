using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Agent;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class SellerContextBuilderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly SellerContextBuilder _builder;
    private readonly Seller _seller;

    public SellerContextBuilderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _builder = new SellerContextBuilder(_db);
        _seller = new Seller { WhatsAppPhoneNumber = "923001110000", BusinessName = "Shop", BusinessType = "Clothing" };
        _db.Sellers.Add(_seller);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Product AddProduct(string name, decimal price = 1000, int? stock = null)
    {
        var p = new Product { SellerId = _seller.Id, Name = name, Price = price, StockQty = stock };
        _db.Products.Add(p);
        _db.SaveChanges();
        return p;
    }

    private Customer AddCustomer(string name, string? city = null)
    {
        var c = new Customer { SellerId = _seller.Id, Name = name, City = city };
        _db.Customers.Add(c);
        _db.SaveChanges();
        return c;
    }

    private Order AddOrder(Customer c, decimal delivery, OrderPaymentMethod method, params string[] items)
    {
        var o = new Order { SellerId = _seller.Id, CustomerId = c.Id, DeliveryCharge = delivery, PaymentMethod = method };
        foreach (var i in items) o.Items.Add(new OrderItem { ProductNameSnapshot = i, UnitPrice = 100, Quantity = 1 });
        _db.Orders.Add(o);
        _db.SaveChanges();
        return o;
    }

    [Fact]
    public async Task Products_AreRankedByTheWordsOfTheMessage_AndATypoStillMatches()
    {
        AddProduct("Kurti");
        var polo = AddProduct("Polo Shirt", 500, 12);
        AddProduct("Dupatta");

        var ctx = await _builder.BuildAsync(_seller, "polo shirt ka price 600 kar do", default);
        Assert.Equal(polo.Id, ctx.Products[0].Id);
        Assert.Equal(1.0, ctx.Products[0].Score);

        var typo = await _builder.BuildAsync(_seller, "Dupata 300 ka kar do", default);
        Assert.Equal("Dupatta", typo.Products[0].Name);
        Assert.Equal(0.7, typo.Products[0].Score);
    }

    [Fact]
    public async Task Products_AreCapped_AndRenderedWithIdsAndPrices()
    {
        for (var i = 0; i < 40; i++) AddProduct("Item" + i);
        var ctx = await _builder.BuildAsync(_seller, "hello", default);
        Assert.Equal(SellerContextBuilder.MaxProducts, ctx.Products.Count);
        Assert.Contains("Rs.1000", ctx.Render());
    }

    [Fact]
    public async Task Customer_IsListedOnlyWhenNamed_WithUsualItemsFromTheirLastOrders()
    {
        var sara = AddCustomer("Sara Khan", "Lahore");
        AddCustomer("Bilal");
        AddOrder(sara, 200, OrderPaymentMethod.Cod, "Kurti", "Dupatta");
        AddOrder(sara, 200, OrderPaymentMethod.Cod, "Kurti");

        var named = await _builder.BuildAsync(_seller, "Sara ko 2 Kurti bhej do", default);
        var hit = Assert.Single(named.Customers);
        Assert.Equal(sara.Id, hit.Id);
        Assert.Equal(2, hit.Orders);
        Assert.Equal("Kurti", hit.UsualItems[0]);

        var none = await _builder.BuildAsync(_seller, "aaj ka hisaab", default);
        Assert.Empty(none.Customers);
    }

    [Fact]
    public async Task AMessageInUrduScript_ListsTheRecentCustomers_BecauseNamesCannotBeRankedAcrossScripts()
    {
        var bilal = AddCustomer("Bilal", "Multan");
        AddCustomer("Sara Khan", "Lahore");

        var urdu = await _builder.BuildAsync(_seller, "بلال کو دو کرتی بھیج دیں", default);
        Assert.Contains(urdu.Customers, c => c.Id == bilal.Id);
        Assert.All(urdu.Customers, c => Assert.Equal(0, c.Score));            // listed for the model to match by sound, not because the words matched

        var roman = await _builder.BuildAsync(_seller, "aaj ka hisaab", default);
        Assert.Empty(roman.Customers);                                         // a Roman-script message still lists only who it names
    }

    [Fact]
    public async Task Habits_NeedAClearMajority_AndAtLeastThreeOrders()
    {
        var c = AddCustomer("Sara");
        AddOrder(c, 200, OrderPaymentMethod.Cod);
        AddOrder(c, 200, OrderPaymentMethod.Cod);
        Assert.Null((await _builder.BuildAsync(_seller, "x", default)).UsualDeliveryCharge);

        AddOrder(c, 200, OrderPaymentMethod.Cod);
        AddOrder(c, 0, OrderPaymentMethod.Manual);
        var ctx = await _builder.BuildAsync(_seller, "x", default);
        Assert.Equal(200m, ctx.UsualDeliveryCharge);
        Assert.Equal("Cod", ctx.UsualPaymentMethod);
        Assert.Equal(4, ctx.PendingOrders);
    }

    [Fact]
    public async Task OtherSellersData_IsNeverIncluded()
    {
        var other = new Seller { WhatsAppPhoneNumber = "923009990000", BusinessName = "Other" };
        _db.Sellers.Add(other);
        await _db.SaveChangesAsync();
        _db.Products.Add(new Product { SellerId = other.Id, Name = "Secret Kurti", Price = 1 });
        _db.Customers.Add(new Customer { SellerId = other.Id, Name = "Secret Sara" });
        await _db.SaveChangesAsync();

        var ctx = await _builder.BuildAsync(_seller, "secret sara kurti", default);
        Assert.Empty(ctx.Products);
        Assert.Empty(ctx.Customers);
    }

    [Fact]
    public async Task RecentActions_SkipUndoneOnes_NewestFirst()
    {
        _db.ActionLogs.Add(new ActionLog { SellerId = _seller.Id, ActionType = ActionType.ExpenseAdded, PayloadJson = "{}" });
        _db.ActionLogs.Add(new ActionLog { SellerId = _seller.Id, ActionType = ActionType.OrderCreated, OrderId = 7, PayloadJson = "{}", Undone = true });
        _db.ActionLogs.Add(new ActionLog { SellerId = _seller.Id, ActionType = ActionType.ProductPriceChanged, PayloadJson = "{}" });
        await _db.SaveChangesAsync();

        var ctx = await _builder.BuildAsync(_seller, "x", default);
        Assert.Equal(new[] { "ProductPriceChanged", "ExpenseAdded" }, ctx.RecentActions);
    }
}
