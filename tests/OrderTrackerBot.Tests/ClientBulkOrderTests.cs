using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class ClientBulkOrderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ClientOrderBulk _orders;
    private readonly ClientCustomerBulk _customers;

    public ClientBulkOrderTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _orders = new ClientOrderBulk(_db);
        _customers = new ClientCustomerBulk(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Seller> SellerAsync(string phone)
    {
        var seller = new Seller { WhatsAppPhoneNumber = phone, BusinessName = "Shop " + phone };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        return seller;
    }

    private async Task<Product> ProductAsync(int sellerId, int? stock)
    {
        var product = new Product { SellerId = sellerId, Name = "Kurti", Price = 1000, StockQty = stock };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        return product;
    }

    private async Task<Order> OrderAsync(Seller seller, Product product, OrderStatus status, int quantity = 2)
    {
        var customer = new Customer { SellerId = seller.Id, Name = "Sara" };
        _db.Customers.Add(customer);
        var order = new Order
        {
            SellerId = seller.Id,
            Customer = customer,
            Status = status,
            Subtotal = 1000 * quantity,
            Total = 1000 * quantity,
            Items = { new OrderItem { ProductId = product.Id, ProductNameSnapshot = product.Name, UnitPrice = 1000, Quantity = quantity } },
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task Shipping_pending_orders_moves_them_and_logs_each_change()
    {
        var seller = await SellerAsync("923001111111");
        var product = await ProductAsync(seller.Id, 10);
        var a = await OrderAsync(seller, product, OrderStatus.Pending);
        var b = await OrderAsync(seller, product, OrderStatus.Pending);

        var result = await _orders.BulkStatusAsync(seller.Id, new ClientOrderBulkInput([a.Id, b.Id], "shipped"), default);

        Assert.Equal(2, result.Updated);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(OrderStatus.Shipped, (await _db.Orders.FindAsync(a.Id))!.Status);
        Assert.NotNull((await _db.Orders.FindAsync(b.Id))!.ShippedAt);
        Assert.Equal(2, await _db.ActionLogs.CountAsync(l => l.ActionType == ActionType.OrderStatusChanged));
    }

    [Fact]
    public async Task Disallowed_moves_are_skipped_and_counted()
    {
        var seller = await SellerAsync("923002222222");
        var product = await ProductAsync(seller.Id, null);
        var cancelled = await OrderAsync(seller, product, OrderStatus.Cancelled);
        var pending = await OrderAsync(seller, product, OrderStatus.Pending);

        var result = await _orders.BulkStatusAsync(seller.Id, new ClientOrderBulkInput([cancelled.Id, pending.Id], "returned"), default);

        Assert.Equal(0, result.Updated);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(OrderStatus.Pending, (await _db.Orders.FindAsync(pending.Id))!.Status);
    }

    [Fact]
    public async Task Returning_a_delivered_order_gives_its_units_back_to_stock()
    {
        var seller = await SellerAsync("923003333333");
        var product = await ProductAsync(seller.Id, 5);
        var order = await OrderAsync(seller, product, OrderStatus.Delivered, quantity: 2);

        await _orders.BulkStatusAsync(seller.Id, new ClientOrderBulkInput([order.Id], "returned"), default);

        Assert.Equal(OrderStatus.Returned, (await _db.Orders.FindAsync(order.Id))!.Status);
        Assert.Equal(7, (await _db.Products.FindAsync(product.Id))!.StockQty);
    }

    [Fact]
    public async Task Another_shops_orders_are_never_touched()
    {
        var mine = await SellerAsync("923004444444");
        var theirs = await SellerAsync("923005555555");
        var product = await ProductAsync(theirs.Id, null);
        var foreign = await OrderAsync(theirs, product, OrderStatus.Pending);

        var result = await _orders.BulkStatusAsync(mine.Id, new ClientOrderBulkInput([foreign.Id], "delivered"), default);

        Assert.Equal(0, result.Updated);
        Assert.Equal(OrderStatus.Pending, (await _db.Orders.FindAsync(foreign.Id))!.Status);
    }

    [Theory]
    [InlineData(null, "shipped")]
    [InlineData(new int[0], "shipped")]
    [InlineData(new[] { 1 }, "cancelled")]
    [InlineData(new[] { 0 }, "shipped")]
    public void Validate_refuses_bad_order_input(int[]? ids, string status)
    {
        Assert.NotNull(ClientOrderBulk.Validate(new ClientOrderBulkInput(ids, status)));
    }

    [Fact]
    public async Task Customer_price_group_is_set_cleared_and_deleted_customers_are_ignored()
    {
        var seller = await SellerAsync("923006666666");
        var live = new Customer { SellerId = seller.Id, Name = "Ali" };
        var deleted = new Customer { SellerId = seller.Id, Name = "Old", DeletedAt = DateTime.UtcNow };
        _db.Customers.AddRange(live, deleted);
        await _db.SaveChangesAsync();

        var updated = await _customers.BulkPriceGroupAsync(seller.Id, new ClientCustomerBulkInput([live.Id, deleted.Id], "b"), default);
        Assert.Equal(1, updated);
        Assert.Equal("B", (await _db.Customers.FindAsync(live.Id))!.PriceGroup);
        Assert.Null((await _db.Customers.FindAsync(deleted.Id))!.PriceGroup);

        await _customers.BulkPriceGroupAsync(seller.Id, new ClientCustomerBulkInput([live.Id], ""), default);
        Assert.Null((await _db.Customers.FindAsync(live.Id))!.PriceGroup);
    }

    [Fact]
    public void Customer_validate_accepts_only_price_groups()
    {
        Assert.Null(ClientCustomerBulk.Validate(new ClientCustomerBulkInput([1], "C")));
        Assert.Null(ClientCustomerBulk.Validate(new ClientCustomerBulkInput([1], null)));
        Assert.NotNull(ClientCustomerBulk.Validate(new ClientCustomerBulkInput([1], "D")));
    }
}
