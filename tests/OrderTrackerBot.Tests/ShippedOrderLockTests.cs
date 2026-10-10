using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ShippedOrderLockTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public ShippedOrderLockTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
    }

    private async Task<int> SeedOrderAsync(AppDbContext db, OrderStatus status)
    {
        var seller = await db.Sellers.FirstAsync();
        var customer = db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" }).Entity;
        await db.SaveChangesAsync();
        var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Status = status, Subtotal = 2000, Total = 2000 };
        order.Items.Add(new OrderItem { ProductNameSnapshot = "Kurti", UnitPrice = 2000, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    [Theory]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Delivered)]
    public async Task DispatchedOrder_RejectsItemAndMoneyEdits(OrderStatus status)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var id = await SeedOrderAsync(db, status);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"edit order {id}", default);
        await engine.HandleIncomingMessageAsync(Phone, "1 = 2", default);
        await engine.HandleIncomingMessageAsync(Phone, "add Kurti 1", default);
        await engine.HandleIncomingMessageAsync(Phone, "price 1 = 500", default);
        await engine.HandleIncomingMessageAsync(Phone, "delivery 300", default);
        await engine.HandleIncomingMessageAsync(Phone, "done", default);
        await engine.HandleIncomingMessageAsync(Phone, $"order {id} delivery 300", default);

        var order = await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id);
        Assert.Equal(1, order.Items.Single().Quantity);
        Assert.Equal(2000m, order.Items.Single().UnitPrice);
        Assert.Equal(1, order.Items.Count);
        Assert.Equal(0m, order.DeliveryCharge);
        Assert.Equal(2000m, order.Total);
        Assert.Contains(_sent, m => m.Contains("nahi badal sakte") || m.Contains("nahi badal sakti"));
    }

    [Fact]
    public async Task DispatchedOrder_StillAcceptsPayment()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var id = await SeedOrderAsync(db, OrderStatus.Shipped);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"order {id} advance 500", default);

        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == id);
        Assert.Equal(500m, order.AmountPaid);
    }

    public void Dispose() => _dbFactory.Dispose();
}
