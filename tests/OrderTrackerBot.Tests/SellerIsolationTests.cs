using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Two sellers share one database. Whatever seller B types, nothing of seller A may come back, and the rows each one created stay theirs.
public class SellerIsolationTests : IDisposable
{
    private const string PhoneA = "923001111111";
    private const string PhoneB = "923002222222";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ConversationEngine _engine;
    private readonly List<(string To, string Text)> _sent = new();

    public SellerIsolationTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((to, text, _) => _sent.Add((to, text))).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((to, text, _, _) => _sent.Add((to, text))).Returns(Task.CompletedTask);
        _engine = new ConversationEngine(_db, new Mock<IAiOrderAssistant>().Object, sender.Object, new Mock<IFounderAlertNotifier>().Object);
    }

    private async Task OnboardAsync(string phone, string shop, string product)
    {
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", shop, "skip", product, "done" })
            await _engine.HandleIncomingMessageAsync(phone, step, default);
    }

    [Fact]
    public async Task SellerB_NeverSeesSellerAsCatalogOrdersOrShop()
    {
        await OnboardAsync(PhoneA, "Ayesha Collections", "Kurti - 1800");
        await OnboardAsync(PhoneB, "Bilal Traders", "Zebra Mug - 500");
        await _engine.HandleIncomingMessageAsync(PhoneA, "walkin Kurti 1 then yes (cash)", default);

        var a = await _db.Sellers.SingleAsync(s => s.WhatsAppPhoneNumber == PhoneA);
        var b = await _db.Sellers.SingleAsync(s => s.WhatsAppPhoneNumber == PhoneB);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("Kurti", Assert.Single(await _db.Products.Where(p => p.SellerId == a.Id).ToListAsync()).Name);
        Assert.Equal("Zebra Mug", Assert.Single(await _db.Products.Where(p => p.SellerId == b.Id).ToListAsync()).Name);
        Assert.NotEmpty(await _db.Orders.Where(o => o.SellerId == a.Id).ToListAsync());
        Assert.Empty(await _db.Orders.Where(o => o.SellerId == b.Id).ToListAsync());

        _sent.Clear();
        foreach (var message in new[]
                 { "catalog", "orders today", "today's summary", "aaj ka hisaab", "order 1", "stock", "export", "customers", "profit", "margin", "expenses", "undo" })
            await _engine.HandleIncomingMessageAsync(PhoneB, message, default);

        var toB = _sent.Where(m => m.To == PhoneB).Select(m => m.Text).ToList();
        Assert.NotEmpty(toB);
        Assert.DoesNotContain(toB, t => t.Contains("Kurti", StringComparison.OrdinalIgnoreCase) || t.Contains("Ayesha", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_sent, m => m.To == PhoneA);
        Assert.Empty(await _db.Orders.Where(o => o.SellerId == b.Id).ToListAsync());
    }

    [Fact]
    public async Task SellerB_CannotChangeOrDeleteSellerAsData()
    {
        await OnboardAsync(PhoneA, "Ayesha Collections", "Kurti - 1800");
        await OnboardAsync(PhoneB, "Bilal Traders", "Zebra Mug - 500");
        await _engine.HandleIncomingMessageAsync(PhoneA, "walkin Kurti 1 then yes (cash)", default);
        var a = await _db.Sellers.SingleAsync(s => s.WhatsAppPhoneNumber == PhoneA);
        var ordersBefore = await _db.Orders.Where(o => o.SellerId == a.Id).CountAsync();

        foreach (var message in new[] { "cancel 1", "mark 1 shipped", "order 1 advance 500", "stock Kurti 5", "edit product: Kurti - 1", "reset account", "yes" })
            await _engine.HandleIncomingMessageAsync(PhoneB, message, default);

        var products = await _db.Products.Where(p => p.SellerId == a.Id).ToListAsync();
        Assert.Equal(1800m, Assert.Single(products).Price);
        Assert.Null(products[0].StockQty);
        var orders = await _db.Orders.Where(o => o.SellerId == a.Id).ToListAsync();
        Assert.Equal(ordersBefore, orders.Count);
        Assert.DoesNotContain(orders, o => o.Status == OrderTrackerBot.Domain.Enums.OrderStatus.Cancelled || o.Status == OrderTrackerBot.Domain.Enums.OrderStatus.Shipped);
        Assert.True((await _db.Sellers.SingleAsync(s => s.WhatsAppPhoneNumber == PhoneA)).OnboardingComplete);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
