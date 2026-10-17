using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Dashboard expenses, wages, losses, stock counts and order creation.</summary>
public sealed class ClientMoneyOutTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ClientQueries _queries;
    private int _sellerId;

    public ClientMoneyOutTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _queries = new ClientQueries(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task SellerAsync()
    {
        var seller = new Seller { WhatsAppPhoneNumber = "923001230001", BusinessName = "Shop", DefaultDeliveryCharge = 150 };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        _sellerId = seller.Id;
    }

    private async Task<Product> ProductAsync(string name, decimal price, int? stock = null, decimal? priceA = null)
    {
        var product = new Product { SellerId = _sellerId, Name = name, Price = price, CostPrice = 300, StockQty = stock, PriceA = priceA };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        return product;
    }

    private async Task<Customer> CustomerAsync(string name, string? group = null)
    {
        var customer = new Customer { SellerId = _sellerId, Name = name, PriceGroup = group };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        return customer;
    }

    private static AdminDateRange ThisWeek()
    {
        var today = AdminDateRange.Today();
        return new AdminDateRange(today.AddDays(-7), today, AdminDateRange.ToUtc(today.AddDays(-7)), AdminDateRange.ToUtc(today.AddDays(1)));
    }

    [Fact]
    public async Task Expenses_are_validated_listed_and_deleted()
    {
        await SellerAsync();
        var expenses = new ClientExpenses(_db);

        Assert.NotNull((await expenses.CreateAsync(_sellerId, new ExpenseInput(0, "packaging", null, null), default)).Problem);
        Assert.NotNull((await expenses.CreateAsync(_sellerId, new ExpenseInput(100, " ", null, null), default)).Problem);
        Assert.NotNull((await expenses.CreateAsync(_sellerId, new ExpenseInput(100, "petrol", null, "2999-01-01"), default)).Problem);

        var (problem, saved) = await expenses.CreateAsync(_sellerId, new ExpenseInput(800, " Petrol ", "rider", null), default);
        Assert.Null(problem);
        Assert.Equal("petrol", saved!.Category);

        var list = await expenses.ListAsync(_sellerId, ThisWeek(), default);
        Assert.Single(list.Items);
        Assert.Equal(800, list.Total);

        Assert.True(await expenses.DeleteAsync(_sellerId, saved.Id, default));
        Assert.False(await expenses.DeleteAsync(_sellerId, saved.Id, default));
    }

    [Fact]
    public async Task Wages_need_a_name_and_a_positive_amount()
    {
        await SellerAsync();
        var wages = new ClientWages(_db);

        Assert.NotNull((await wages.CreateAsync(_sellerId, new WageInput("", 1000, 5, null), default)).Problem);
        Assert.NotNull((await wages.CreateAsync(_sellerId, new WageInput("Ali", -5, 5, null), default)).Problem);

        var (problem, saved) = await wages.CreateAsync(_sellerId, new WageInput("Ali", 12000, 20, null), default);
        Assert.Null(problem);
        Assert.Equal(12000, (await wages.ListAsync(_sellerId, ThisWeek(), default)).Total);
        Assert.True(await wages.DeleteAsync(_sellerId, saved!.Id, default));
    }

    [Fact]
    public async Task A_loss_takes_units_out_of_tracked_stock_only()
    {
        await SellerAsync();
        var tracked = await ProductAsync("Kurti", 1000, stock: 10);
        var untracked = await ProductAsync("Sticker", 20);
        var losses = new ClientLosses(_db);

        var (problem, loss) = await losses.CreateAsync(_sellerId, new LossInput(tracked.Id, null, 3, "damaged"), default);
        Assert.Null(problem);
        Assert.Equal(300, loss!.UnitCost);
        Assert.Equal(7, (await _db.Products.FindAsync(tracked.Id))!.StockQty);

        await losses.CreateAsync(_sellerId, new LossInput(untracked.Id, null, 2, null), default);
        Assert.Null((await _db.Products.FindAsync(untracked.Id))!.StockQty);

        Assert.NotNull((await losses.CreateAsync(_sellerId, new LossInput(null, null, 1, null), default)).Problem);
    }

    [Fact]
    public async Task Stock_can_be_set_only_on_tracked_products()
    {
        await SellerAsync();
        var tracked = await ProductAsync("Kurti", 1000, stock: 4);
        var untracked = await ProductAsync("Sticker", 20);
        var stock = new ClientStock(_db);

        var (problem, saved) = await stock.SetAsync(_sellerId, tracked.Id, new StockInput(25), default);
        Assert.Null(problem);
        Assert.Equal(25, saved!.StockQty);
        Assert.NotNull((await stock.SetAsync(_sellerId, untracked.Id, new StockInput(5), default)).Problem);
        Assert.NotNull((await stock.SetAsync(_sellerId, tracked.Id, new StockInput(-1), default)).Problem);
    }

    [Fact]
    public async Task Dashboard_order_uses_catalog_or_price_group_prices_discount_and_stock()
    {
        await SellerAsync();
        var kurti = await ProductAsync("Kurti", 1000, stock: 10, priceA: 900);
        var customer = await CustomerAsync("Sara", "A");
        _db.Discounts.Add(new Discount { SellerId = _sellerId, Code = "SAVE10", Type = DiscountType.Percent, Value = 10 });
        await _db.SaveChangesAsync();
        var create = new ClientOrderCreate(_db, _queries);

        var (problem, order) = await create.CreateAsync(_sellerId, new NewOrderInput(
            customer.Id, [new NewOrderLineInput(kurti.Id, 2)], "Cod", 0, null, "save10", "gift wrap"), default);

        Assert.Null(problem);
        Assert.Equal(1800, order!.Subtotal);      // group A price 900 x 2
        Assert.Equal(180, order.DiscountAmount);  // 10 %
        Assert.Equal(1770, order.Total);          // 1800 - 180 + delivery 150 (seller default)
        Assert.Equal("SAVE10", order.DiscountCode);
        Assert.Equal("Pending", order.Status);
        Assert.Equal(8, (await _db.Products.FindAsync(kurti.Id))!.StockQty);  // 2 units held by the new order
        Assert.Equal(1, await _db.ActionLogs.CountAsync(l => l.ActionType == ActionType.OrderCreated && l.OrderId == order.Id));
    }

    [Fact]
    public async Task Dashboard_order_refuses_bad_input_without_saving()
    {
        await SellerAsync();
        var kurti = await ProductAsync("Kurti", 1000, stock: 10);
        var customer = await CustomerAsync("Sara");
        var create = new ClientOrderCreate(_db, _queries);

        Assert.NotNull((await create.CreateAsync(_sellerId, new NewOrderInput(null, [new NewOrderLineInput(kurti.Id, 1)], null, null, null, null, null), default)).Problem);
        Assert.NotNull((await create.CreateAsync(_sellerId, new NewOrderInput(customer.Id, [], null, null, null, null, null), default)).Problem);
        Assert.NotNull((await create.CreateAsync(_sellerId, new NewOrderInput(customer.Id, [new NewOrderLineInput(kurti.Id, 1)], "Crypto", null, null, null, null), default)).Problem);
        Assert.NotNull((await create.CreateAsync(_sellerId, new NewOrderInput(customer.Id, [new NewOrderLineInput(kurti.Id, 1)], null, 99999, null, null, null), default)).Problem);
        Assert.NotNull((await create.CreateAsync(_sellerId, new NewOrderInput(customer.Id, [new NewOrderLineInput(kurti.Id, 1)], null, null, null, "NOPE", null), default)).Problem);

        Assert.Equal(0, await _db.Orders.CountAsync());
        Assert.Equal(10, (await _db.Products.FindAsync(kurti.Id))!.StockQty);
    }
}
