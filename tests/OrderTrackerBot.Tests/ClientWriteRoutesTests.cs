using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>The dashboard's write routes for settings, payment methods, discounts, customers, order status, categories and CSV export.</summary>
public sealed class ClientWriteRoutesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ClientQueries _queries;
    private int _sellerId;

    public ClientWriteRoutesTests()
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
        var seller = new Seller { WhatsAppPhoneNumber = "923001230000", BusinessName = "Shop" };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        _sellerId = seller.Id;
    }

    private async Task<Product> ProductAsync(string name, string? category, int? stock = null)
    {
        var product = new Product { SellerId = _sellerId, Name = name, Price = 1000, Category = category, StockQty = stock };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        return product;
    }

    private async Task<Order> OrderAsync(Product product, OrderStatus status, int quantity)
    {
        var customer = new Customer { SellerId = _sellerId, Name = "Sara" };
        _db.Customers.Add(customer);
        var order = new Order
        {
            SellerId = _sellerId, Customer = customer, Status = status,
            Subtotal = 1000 * quantity, Total = 1000 * quantity, CreatedAt = DateTime.UtcNow,
            Items = { new OrderItem { ProductId = product.Id, ProductNameSnapshot = product.Name, UnitPrice = 1000, Quantity = quantity } },
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return order;
    }

    private static SellerSettingsInput Settings(string? email = null, string? receiptSize = null, decimal? tax = null, string? ntn = null) =>
        new("Shop", "Lahore", "Clothing", email, "@shop", 150, tax, ntn, null, receiptSize, null, null, null);

    [Fact]
    public async Task Settings_round_trip_and_reject_bad_values()
    {
        await SellerAsync();
        var settings = new ClientSettings(_db);

        var (problem, saved) = await settings.UpdateAsync(_sellerId, Settings(email: "owner@shop.pk", receiptSize: "80mm", tax: 16), default);
        Assert.Null(problem);
        Assert.Equal("shop", saved!.InstagramHandle ?? "shop");
        Assert.Equal("80mm", saved.ReceiptSize);
        Assert.Equal(16, saved.SalesTaxRate);

        Assert.NotNull(ClientSettings.Validate(Settings(email: "not-an-email")));
        Assert.NotNull(ClientSettings.Validate(Settings(receiptSize: "A3")));
        Assert.NotNull(ClientSettings.Validate(Settings(tax: 150)));
        Assert.NotNull(ClientSettings.Validate(Settings(ntn: "12-34")));
    }

    [Fact]
    public async Task Payment_methods_are_added_once_and_removed_only_by_their_owner()
    {
        await SellerAsync();
        var settings = new ClientSettings(_db);

        var (problem, method) = await settings.AddPaymentMethodAsync(_sellerId, new PaymentMethodInput("jazzcash", "03001234567"), default);
        Assert.Null(problem);
        Assert.Equal("JazzCash", method!.Type);

        var (duplicate, _) = await settings.AddPaymentMethodAsync(_sellerId, new PaymentMethodInput("JazzCash", "03001234567"), default);
        Assert.NotNull(duplicate);
        var (unknown, _) = await settings.AddPaymentMethodAsync(_sellerId, new PaymentMethodInput("Crypto", "x"), default);
        Assert.NotNull(unknown);

        Assert.False(await settings.RemovePaymentMethodAsync(_sellerId + 99, method.Id, default));
        Assert.True(await settings.RemovePaymentMethodAsync(_sellerId, method.Id, default));
    }

    [Fact]
    public async Task Discounts_follow_the_create_discount_rules()
    {
        await SellerAsync();
        var discounts = new ClientDiscounts(_db);
        var future = AdminDateRange.Today().AddDays(10).ToString(AdminDateRange.DateFormat);

        var (problem, created) = await discounts.CreateAsync(_sellerId, new DiscountInput("eid10", "Percent", 10, future, null), default);
        Assert.Null(problem);
        Assert.Equal("EID10", created!.Code);
        Assert.Equal(future, created.ExpiresAt);
        Assert.True(created.IsActive);

        Assert.NotNull((await discounts.CreateAsync(_sellerId, new DiscountInput("EID10", "Percent", 5, null, null), default)).Problem);
        Assert.NotNull((await discounts.CreateAsync(_sellerId, new DiscountInput("NEW1", "Percent", 150, null, null), default)).Problem);
        Assert.NotNull((await discounts.CreateAsync(_sellerId, new DiscountInput("NEW2", "Flat", 0, null, null), default)).Problem);
        Assert.NotNull((await discounts.CreateAsync(_sellerId, new DiscountInput("NEW3", "Percent", 5, "2001-01-01", null), default)).Problem);

        var off = await discounts.SetActiveAsync(_sellerId, created.Id, false, default);
        Assert.False(off!.IsActive);
        Assert.Null(await discounts.SetActiveAsync(_sellerId + 99, created.Id, true, default));
    }

    [Fact]
    public async Task Customers_are_validated_and_soft_deleted()
    {
        await SellerAsync();
        var customers = new ClientCustomers(_db, _queries);

        Assert.NotNull(ClientCustomers.Validate(new CustomerInput("", null, null, null, null, null, null)));
        Assert.NotNull(ClientCustomers.Validate(new CustomerInput("Ali", "abc", null, null, null, null, null)));
        Assert.NotNull(ClientCustomers.Validate(new CustomerInput("Ali", null, null, null, null, null, "D")));

        var (problem, created) = await customers.CreateAsync(_sellerId, new CustomerInput(" Ali ", "0300 1234567", "Lahore", null, null, null, "b"), default);
        Assert.Null(problem);
        Assert.Equal("Ali", created!.Name);
        Assert.Equal("B", created.PriceGroup);

        var (updateProblem, updated) = await customers.UpdateAsync(_sellerId, created.Id, new CustomerInput("Ali Khan", "0300 1234567", "Karachi", null, null, null, null), default);
        Assert.Null(updateProblem);
        Assert.Equal("Ali Khan", updated!.Name);
        Assert.Null(updated.PriceGroup);

        Assert.True(await customers.DeleteAsync(_sellerId, created.Id, default));
        Assert.Null(await _queries.CustomerAsync(_sellerId, created.Id, default));
        Assert.False(await customers.DeleteAsync(_sellerId, created.Id, default));
    }

    [Fact]
    public async Task Order_status_follows_the_dashboard_rules_and_moves_stock()
    {
        await SellerAsync();
        // The active order already holds its 2 units, so 8 left in stock; cancelling gives them back.
        var product = await ProductAsync("Kurti", "Tops", stock: 8);
        var order = await OrderAsync(product, OrderStatus.Pending, quantity: 2);
        var changer = new ClientOrderStatus(_db, _queries);

        var (cancelProblem, cancelled) = await changer.ChangeAsync(_sellerId, order.Id, "Cancelled", default);
        Assert.Null(cancelProblem);
        Assert.Equal("Cancelled", cancelled!.Status);
        Assert.Equal(10, (await _db.Products.FindAsync(product.Id))!.StockQty);

        var (refused, _) = await changer.ChangeAsync(_sellerId, order.Id, "Shipped", default);
        Assert.NotNull(refused);
        Assert.NotNull((await changer.ChangeAsync(_sellerId, order.Id, "Pending", default)).Problem);
        Assert.NotNull((await changer.ChangeAsync(_sellerId + 99, order.Id, "Shipped", default)).Problem);
    }

    [Fact]
    public async Task Returning_a_shipped_order_puts_its_units_back()
    {
        await SellerAsync();
        var product = await ProductAsync("Kurti", "Tops", stock: 3);
        var order = await OrderAsync(product, OrderStatus.Pending, quantity: 2);
        var changer = new ClientOrderStatus(_db, _queries);

        await changer.ChangeAsync(_sellerId, order.Id, "Shipped", default);
        Assert.Equal(3, (await _db.Products.FindAsync(product.Id))!.StockQty);
        await changer.ChangeAsync(_sellerId, order.Id, "Returned", default);
        Assert.Equal(5, (await _db.Products.FindAsync(product.Id))!.StockQty);
    }

    [Fact]
    public async Task Category_rename_moves_products_and_refuses_uncategorized()
    {
        await SellerAsync();
        await ProductAsync("Shirt", "Tops");
        await ProductAsync("Kurti", "Tops");
        await ProductAsync("Shoe", "Footwear");
        var rename = new ClientCategoryRename(_db);

        var (problem, updated) = await rename.RenameAsync(_sellerId, new CategoryRenameInput("Tops", "Upper wear"), default);
        Assert.Null(problem);
        Assert.Equal(2, updated);
        Assert.Equal(2, await _db.Products.CountAsync(p => p.SellerId == _sellerId && p.Category == "Upper wear"));

        Assert.NotNull((await rename.RenameAsync(_sellerId, new CategoryRenameInput("Uncategorized", "X"), default)).Problem);
        Assert.NotNull((await rename.RenameAsync(_sellerId, new CategoryRenameInput("Footwear", "Footwear"), default)).Problem);
    }

    [Fact]
    public async Task Export_quotes_cells_neutralises_formulas_and_adds_a_bom()
    {
        await SellerAsync();
        var product = await ProductAsync("=HYPERLINK(\"x\")", "Tops");
        await OrderAsync(product, OrderStatus.Delivered, quantity: 1);
        var export = new ClientExport(_db);
        var range = new AdminDateRange(
            AdminDateRange.Today().AddDays(-1), AdminDateRange.Today().AddDays(1),
            AdminDateRange.ToUtc(AdminDateRange.Today().AddDays(-1)), AdminDateRange.ToUtc(AdminDateRange.Today().AddDays(2)));

        var (fileName, content) = await export.BuildAsync(_sellerId, "order-items", range, default);

        Assert.StartsWith("﻿", content);
        Assert.StartsWith("order-items_", fileName);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", content);
        Assert.Contains("\"Product\"", content.Split("\r\n")[0]);
    }

    [Fact]
    public void Export_only_offers_the_four_datasets()
    {
        Assert.Equal(["orders", "order-items", "customers", "catalog"], ClientExport.Datasets);
    }
}
