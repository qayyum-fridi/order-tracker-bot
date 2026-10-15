using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Tests;

public sealed class ClientMessagesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ClientMessagesTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Shows_only_this_sellers_chat_oldest_first()
    {
        var seller = new Seller { WhatsAppPhoneNumber = "923001111111" };
        _db.Sellers.Add(seller);
        _db.Sellers.Add(new Seller { WhatsAppPhoneNumber = "923002222222" });
        await _db.SaveChangesAsync();
        await AddAsync("923001111111", "inbound", "Kurti ka price?");
        await AddAsync("923002222222", "inbound", "another shop");
        await AddAsync("923001111111", "outbound", "Rs 1800");

        var messages = await new ClientQueries(_db).MessagesAsync(seller.Id, 0, 50, default);

        Assert.Equal(new[] { "Kurti ka price?", "Rs 1800" }, messages.Select(m => m.Text));
        Assert.Equal(new[] { "inbound", "outbound" }, messages.Select(m => m.Direction));
    }

    [Fact]
    public async Task With_afterId_returns_only_newer_lines_and_respects_the_limit()
    {
        var seller = new Seller { WhatsAppPhoneNumber = "923001111111" };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        var first = await AddAsync("923001111111", "inbound", "one");
        await AddAsync("923001111111", "outbound", "two");
        await AddAsync("923001111111", "inbound", "three");

        var newer = await new ClientQueries(_db).MessagesAsync(seller.Id, first.Id, 50, default);
        var capped = await new ClientQueries(_db).MessagesAsync(seller.Id, 0, 1, default);

        Assert.Equal(new[] { "two", "three" }, newer.Select(m => m.Text));
        Assert.Equal(new[] { "three" }, capped.Select(m => m.Text));
    }

    [Fact]
    public async Task Unknown_seller_gets_an_empty_chat()
    {
        Assert.Empty(await new ClientQueries(_db).MessagesAsync(999, 0, 50, default));
    }

    private async Task<MessageLog> AddAsync(string phone, string direction, string text)
    {
        var row = new MessageLog { Phone = phone, Direction = direction, RawText = text };
        _db.MessageLogs.Add(row);
        await _db.SaveChangesAsync();
        return row;
    }
}
