using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class PilotReportTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    public PilotReportTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    private void Log(string phone, string direction, string text, double daysAgo) =>
        _db.MessageLogs.Add(new MessageLog { Phone = phone, Direction = direction, RawText = text, CreatedAt = Now.AddDays(-daysAgo) });

    [Fact]
    public async Task CountsMessagesNotUnderstoodVoiceAndWhoCameBack()
    {
        var returning = new Seller { WhatsAppPhoneNumber = "923001111111", BusinessName = "Ayesha" };
        var fresh = new Seller { WhatsAppPhoneNumber = "923002222222", BusinessName = "Bilal", Plan = SubscriptionPlan.Pro, SubscriptionActiveUntil = Now.AddDays(10) };
        _db.Sellers.AddRange(returning, fresh);
        Log(returning.WhatsAppPhoneNumber, "inbound", "orders today", 10);   // previous window
        Log(returning.WhatsAppPhoneNumber, "inbound", "kuch", 2);
        Log(returning.WhatsAppPhoneNumber, "outbound", "Maaf kijiye, samajh nahi aaya", 2);
        Log(returning.WhatsAppPhoneNumber, "outbound", "🎤 Maine suna: orders", 1);
        Log(fresh.WhatsAppPhoneNumber, "inbound", "start", 1);
        await _db.SaveChangesAsync();

        var report = await PilotReport.BuildAsync(_db, 7, Now, default);

        var a = report.Sellers.Single(s => s.Phone == returning.WhatsAppPhoneNumber);
        Assert.Equal(1, a.Messages);
        Assert.Equal(1, a.NotUnderstood);
        Assert.Equal(1, a.VoiceNotes);
        Assert.True(a.Returned);
        var b = report.Sellers.Single(s => s.Phone == fresh.WhatsAppPhoneNumber);
        Assert.False(b.Returned);
        Assert.True(b.Paid);
        Assert.Equal(2, report.Totals.ActiveSellers);
        Assert.Equal(1, report.Totals.Returned);
        Assert.Equal(1, report.Totals.Paid);
    }

    [Fact]
    public async Task EditOrCancelWithinAnHourOfSaving_CountsAsAFix_LaterOnesDoNot()
    {
        var seller = new Seller { WhatsAppPhoneNumber = "923001111111" };
        _db.Sellers.Add(seller);
        await _db.SaveChangesAsync();
        var customer = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "923009999999" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        var quick = new Order { SellerId = seller.Id, CustomerId = customer.Id, CreatedAt = Now.AddDays(-1) };
        var slow = new Order { SellerId = seller.Id, CustomerId = customer.Id, CreatedAt = Now.AddDays(-2) };
        _db.Orders.AddRange(quick, slow);
        await _db.SaveChangesAsync();
        _db.ActionLogs.Add(new ActionLog { SellerId = seller.Id, ActionType = ActionType.OrderEdited, OrderId = quick.Id, PayloadJson = "{}", CreatedAt = quick.CreatedAt.AddMinutes(5) });
        _db.ActionLogs.Add(new ActionLog { SellerId = seller.Id, ActionType = ActionType.OrderCancelled, OrderId = slow.Id, PayloadJson = "{}", CreatedAt = slow.CreatedAt.AddHours(5) });
        await _db.SaveChangesAsync();

        var report = await PilotReport.BuildAsync(_db, 7, Now, default);

        Assert.Equal(2, report.Totals.OrdersSaved);
        Assert.Equal(1, report.Totals.EditedOrCancelledWithinAnHour);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
