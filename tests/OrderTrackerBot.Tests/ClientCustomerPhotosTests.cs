using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public sealed class ClientCustomerPhotosTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ClientCustomerPhotos _photos;
    private int _sellerId;
    private int _customerId;

    public ClientCustomerPhotosTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _photos = new ClientCustomerPhotos(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task CustomerAsync()
    {
        var seller = new Seller { WhatsAppPhoneNumber = "923001230002", BusinessName = "Shop" };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        var customer = new Customer { SellerId = seller.Id, Name = "Sara" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        _sellerId = seller.Id;
        _customerId = customer.Id;
    }

    [Fact]
    public async Task A_new_upload_replaces_the_previous_photo()
    {
        await CustomerAsync();
        Assert.Null(await _photos.SetAsync(_sellerId, _customerId, Jpeg, default));
        Assert.Null(await _photos.SetAsync(_sellerId, _customerId, Png, default));

        var photo = await _photos.GetAsync(_sellerId, _customerId, default);
        Assert.Equal("image/png", photo!.ContentType);
        Assert.Equal(1, await _db.CustomerPhotos.CountAsync());
    }

    [Fact]
    public async Task Photos_are_judged_by_their_bytes_and_size_and_scoped_to_the_seller()
    {
        await CustomerAsync();
        Assert.NotNull(ClientCustomerPhotos.Validate([0x25, 0x50, 0x44, 0x46]));
        Assert.NotNull(ClientCustomerPhotos.Validate([]));
        Assert.NotNull(ClientCustomerPhotos.Validate(new byte[ClientCustomerPhotos.MaxBytes + 1]));
        Assert.NotNull(await _photos.SetAsync(_sellerId + 99, _customerId, Jpeg, default));
        Assert.Null(await _photos.SetAsync(_sellerId, _customerId, Jpeg, default));
        Assert.False(await _photos.DeleteAsync(_sellerId + 99, _customerId, default));
        Assert.True(await _photos.DeleteAsync(_sellerId, _customerId, default));
    }
}
