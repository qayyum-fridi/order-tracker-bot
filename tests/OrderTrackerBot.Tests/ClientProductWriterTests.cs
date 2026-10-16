using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class ClientProductWriterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ClientProductWriter _writer;

    public ClientProductWriterTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _writer = new ClientProductWriter(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static ClientProductInput Input(
        string? name = "Lawn Suit", decimal? price = 3500, string? sku = null, string? defaultGroup = null, string? type = null,
        int? stock = null, string? category = null) =>
        new(name, price, null, null, null, "piece", 1m, category, null, null, sku, stock, null, true, defaultGroup, type);

    private async Task<Seller> SellerAsync(string phone)
    {
        var seller = new Seller { WhatsAppPhoneNumber = phone, BusinessName = "Shop " + phone };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        return seller;
    }

    [Fact]
    public async Task A_blank_sku_gets_an_automatic_one_and_the_new_fields_are_saved()
    {
        var seller = await SellerAsync("923000000101");

        var result = await _writer.CreateAsync(seller.Id, Input(defaultGroup: "b", type: "service"), default);

        Assert.Equal(200, result.Status);
        var saved = await _db.Products.SingleAsync(p => p.Id == result.Id);
        Assert.StartsWith("SKU-", saved.Sku);
        Assert.Equal("B", saved.DefaultPriceGroup);
        Assert.Equal("service", saved.ProductType);
        Assert.Null(saved.StockQty);
    }

    [Fact]
    public async Task Missing_name_and_bad_choices_are_refused_with_a_clear_reason()
    {
        var seller = await SellerAsync("923000000102");

        var noName = await _writer.CreateAsync(seller.Id, Input(name: "  "), default);
        var badGroup = await _writer.CreateAsync(seller.Id, Input(defaultGroup: "D"), default);
        var badType = await _writer.CreateAsync(seller.Id, Input(type: "rent"), default);
        var negativeStock = await _writer.CreateAsync(seller.Id, Input(stock: -1), default);

        Assert.Equal(400, noName.Status);
        Assert.Equal("Enter the product name.", noName.Error);
        Assert.Equal(400, badGroup.Status);
        Assert.Equal(400, badType.Status);
        Assert.Equal(400, negativeStock.Status);
        Assert.Empty(await _db.Products.ToListAsync());
    }

    [Fact]
    public async Task A_sku_already_used_by_this_shop_is_refused_whatever_the_case()
    {
        var seller = await SellerAsync("923000000103");
        await _writer.CreateAsync(seller.Id, Input(sku: "LAWN-01"), default);

        var duplicate = await _writer.CreateAsync(seller.Id, Input(name: "Other", sku: "lawn-01"), default);

        Assert.Equal(409, duplicate.Status);
    }

    [Fact]
    public async Task Another_shop_may_use_the_same_sku()
    {
        var first = await SellerAsync("923000000104");
        var second = await SellerAsync("923000000105");
        await _writer.CreateAsync(first.Id, Input(sku: "SHARED-1"), default);

        var result = await _writer.CreateAsync(second.Id, Input(sku: "SHARED-1"), default);

        Assert.Equal(200, result.Status);
    }

    [Fact]
    public async Task Updating_keeps_a_product_s_own_sku_and_clears_fields_left_empty()
    {
        var seller = await SellerAsync("923000000106");
        var created = await _writer.CreateAsync(seller.Id, Input(sku: "KEEP-1", category: "Lawn"), default);

        var updated = await _writer.UpdateAsync(seller.Id, created.Id!.Value, Input(name: "Lawn Suit 2", sku: "KEEP-1", category: null), default);

        Assert.Equal(200, updated.Status);
        var saved = await _db.Products.SingleAsync(p => p.Id == created.Id);
        Assert.Equal("Lawn Suit 2", saved.Name);
        Assert.Equal("KEEP-1", saved.Sku);
        Assert.Null(saved.Category);
    }

    [Fact]
    public async Task Another_shop_s_product_cannot_be_changed_or_hidden()
    {
        var owner = await SellerAsync("923000000107");
        var other = await SellerAsync("923000000108");
        var product = await _writer.CreateAsync(owner.Id, Input(), default);

        var update = await _writer.UpdateAsync(other.Id, product.Id!.Value, Input(name: "Hijack"), default);
        var hide = await _writer.DeactivateAsync(other.Id, product.Id!.Value, default);

        Assert.Equal(404, update.Status);
        Assert.Equal(404, hide.Status);
        Assert.True((await _db.Products.SingleAsync(p => p.Id == product.Id)).IsActive);
    }

    [Fact]
    public async Task Hiding_a_product_keeps_it_in_the_database()
    {
        var seller = await SellerAsync("923000000109");
        var product = await _writer.CreateAsync(seller.Id, Input(), default);

        var hide = await _writer.DeactivateAsync(seller.Id, product.Id!.Value, default);

        Assert.Equal(200, hide.Status);
        Assert.False((await _db.Products.SingleAsync(p => p.Id == product.Id)).IsActive);
    }

    [Fact]
    public async Task A_stock_number_turns_tracking_on_and_none_turns_it_off()
    {
        var seller = await SellerAsync("923000000110");
        var product = await _writer.CreateAsync(seller.Id, Input(stock: 12), default);
        Assert.Equal(12, (await _db.Products.SingleAsync(p => p.Id == product.Id)).StockQty);

        await _writer.UpdateAsync(seller.Id, product.Id!.Value, Input(stock: null), default);

        Assert.Null((await _db.Products.SingleAsync(p => p.Id == product.Id)).StockQty);
    }
}
