using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class ClientProductBulkTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ClientProductBulk _bulk;

    public ClientProductBulkTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _bulk = new ClientProductBulk(_db);
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

    private async Task<Product> ProductAsync(int sellerId, string name, string? category = null)
    {
        var product = new Product { SellerId = sellerId, Name = name, Price = 100, Category = category, IsActive = true, CreatedAt = DateTime.UtcNow };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        return product;
    }

    [Fact]
    public async Task Hide_and_show_change_only_the_selected_products_of_this_shop()
    {
        var mine = await SellerAsync("923000000201");
        var theirs = await SellerAsync("923000000202");
        var a = await ProductAsync(mine.Id, "A");
        var b = await ProductAsync(mine.Id, "B");
        var other = await ProductAsync(theirs.Id, "Other");

        var hidden = await _bulk.BulkAsync(mine.Id, new ClientProductBulkInput(new[] { a.Id, b.Id, other.Id }, "hide", null), default);

        Assert.Equal(2, hidden);
        Assert.False((await _db.Products.SingleAsync(p => p.Id == a.Id)).IsActive);
        Assert.True((await _db.Products.SingleAsync(p => p.Id == other.Id)).IsActive);

        await _bulk.BulkAsync(mine.Id, new ClientProductBulkInput(new[] { a.Id }, "show", null), default);
        Assert.True((await _db.Products.SingleAsync(p => p.Id == a.Id)).IsActive);
    }

    [Fact]
    public async Task Category_is_set_trimmed_and_cleared_when_blank()
    {
        var seller = await SellerAsync("923000000203");
        var a = await ProductAsync(seller.Id, "A", "Old");

        await _bulk.BulkAsync(seller.Id, new ClientProductBulkInput(new[] { a.Id }, "category", "  Lawn  "), default);
        Assert.Equal("Lawn", (await _db.Products.SingleAsync(p => p.Id == a.Id)).Category);

        await _bulk.BulkAsync(seller.Id, new ClientProductBulkInput(new[] { a.Id }, "category", " "), default);
        Assert.Null((await _db.Products.SingleAsync(p => p.Id == a.Id)).Category);
    }

    [Fact]
    public void Bad_requests_are_refused_with_a_reason()
    {
        Assert.Equal("Select at least one product.", ClientProductBulk.Validate(new ClientProductBulkInput(Array.Empty<int>(), "hide", null)));
        Assert.Equal("Choose hide, show or category.", ClientProductBulk.Validate(new ClientProductBulkInput(new[] { 1 }, "delete", null)));
        Assert.Equal("Product numbers must be positive.", ClientProductBulk.Validate(new ClientProductBulkInput(new[] { 0 }, "hide", null)));
        Assert.Null(ClientProductBulk.Validate(new ClientProductBulkInput(new[] { 1 }, "hide", null)));
    }
}
