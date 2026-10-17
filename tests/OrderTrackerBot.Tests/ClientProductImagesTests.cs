using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class ClientProductImagesTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8];

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ClientProductImages _images;

    public ClientProductImagesTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _images = new ClientProductImages(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<Product> ProductAsync(string phone)
    {
        var seller = new Seller { WhatsAppPhoneNumber = phone, BusinessName = "Shop " + phone };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        var product = new Product { SellerId = seller.Id, Name = "Kurti", Price = 900 };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        return product;
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 }, null)] // PDF
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, null)] // WAVE, not WebP
    public void Detects_only_supported_pictures_from_their_bytes(byte[] head, string? expected)
    {
        Assert.Equal(expected, ClientProductImages.DetectContentType(head));
    }

    [Fact]
    public void Detects_webp()
    {
        Assert.Equal("image/webp", ClientProductImages.DetectContentType(Webp));
    }

    [Fact]
    public async Task Saves_a_photo_and_lists_it_for_its_own_product_only()
    {
        var product = await ProductAsync("923007777777");
        var (problem, id) = await _images.AddAsync(product.SellerId, product.Id, Png, default);

        Assert.Null(problem);
        Assert.True(id > 0);
        var list = await _images.ListAsync(product.SellerId, product.Id, default);
        Assert.Single(list);
        Assert.Equal("image/png", list[0].ContentType);
        Assert.Empty(await _images.ListAsync(product.SellerId + 99, product.Id, default));
    }

    [Fact]
    public async Task Refuses_other_shops_products_oversized_and_non_pictures()
    {
        var product = await ProductAsync("923008888888");

        Assert.NotNull((await _images.AddAsync(product.SellerId + 99, product.Id, Jpeg, default)).Problem);
        Assert.NotNull((await _images.AddAsync(product.SellerId, product.Id, new byte[ClientProductImages.MaxBytes + 1], default)).Problem);
        Assert.NotNull((await _images.AddAsync(product.SellerId, product.Id, [0x25, 0x50, 0x44, 0x46], default)).Problem);
        Assert.NotNull((await _images.AddAsync(product.SellerId, product.Id, [], default)).Problem);
    }

    [Fact]
    public async Task Caps_photos_per_product_and_delete_is_scoped()
    {
        var product = await ProductAsync("923009999999");
        var ids = new List<int>();
        for (var i = 0; i < ClientProductImages.MaxPerProduct; i++)
            ids.Add((await _images.AddAsync(product.SellerId, product.Id, Jpeg, default)).Id);

        Assert.NotNull((await _images.AddAsync(product.SellerId, product.Id, Jpeg, default)).Problem);
        Assert.False(await _images.DeleteAsync(product.SellerId + 99, product.Id, ids[0], default));
        Assert.True(await _images.DeleteAsync(product.SellerId, product.Id, ids[0], default));
        Assert.Null((await _images.AddAsync(product.SellerId, product.Id, Jpeg, default)).Problem);
    }
}
