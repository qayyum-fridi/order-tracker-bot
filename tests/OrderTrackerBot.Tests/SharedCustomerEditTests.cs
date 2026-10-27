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

/// <summary>
/// The customer record is shared by all of a buyer's orders. A phone, address or name edit made inside one order asks first when the
/// buyer has other orders; "haan" applies it to the record, "nahi" leaves the record as it was.
/// </summary>
public class SharedCustomerEditTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public SharedCustomerEditTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
    }

    /// <summary>Sara with <paramref name="orderCount"/> orders; returns the id of the first.</summary>
    private async Task<int> SeedSaraAsync(AppDbContext db, int orderCount)
    {
        var seller = await db.Sellers.FirstAsync();
        var customer = db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222", Address = "Old street" }).Entity;
        await db.SaveChangesAsync();
        var firstId = 0;
        for (var i = 0; i < orderCount; i++)
        {
            var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Status = OrderStatus.Pending, Subtotal = 1800, Total = 1800 };
            order.Items.Add(new OrderItem { ProductNameSnapshot = "Kurti", UnitPrice = 1800, Quantity = 1 });
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            firstId = firstId == 0 ? order.Id : firstId;
        }
        return firstId;
    }

    [Fact]
    public async Task ACustomerWithOneOrder_IsChangedAtOnce_WithoutAsking()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var id = await SeedSaraAsync(db, orderCount: 1);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"edit order {id}", default);
        await engine.HandleIncomingMessageAsync(Phone, "phone 0300-9998888", default);

        Assert.Equal("03009998888", (await db.Customers.SingleAsync(c => c.Name == "Sara")).Phone);
        Assert.DoesNotContain(_sent, m => m.Contains("orders bhi hain"));
    }

    [Fact]
    public async Task ASharedCustomer_AsksFirst_AndAYesAppliesTheChangeToTheRecord()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var id = await SeedSaraAsync(db, orderCount: 2);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"edit order {id}", default);
        await engine.HandleIncomingMessageAsync(Phone, "phone 0300-9998888", default);

        Assert.Contains(_sent, m => m.Contains("1 aur orders bhi hain") && m.Contains("haan"));
        Assert.Equal("03001112222", (await db.Customers.SingleAsync(c => c.Name == "Sara")).Phone);

        await engine.HandleIncomingMessageAsync(Phone, "haan", default);

        Assert.Equal("03009998888", (await db.Customers.SingleAsync(c => c.Name == "Sara")).Phone);
    }

    [Fact]
    public async Task ASharedCustomer_ANoKeepsTheRecord_AndEditingCarriesOn()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var id = await SeedSaraAsync(db, orderCount: 2);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"edit order {id}", default);
        await engine.HandleIncomingMessageAsync(Phone, "address Naya ghar 5, Johar Town", default);
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "nahi", default);

        Assert.Contains(_sent, m => m.Contains("customer ka record nahi badla"));
        Assert.Equal("Old street", (await db.Customers.SingleAsync(c => c.Name == "Sara")).Address);

        // Still in edit mode: a real item change works on the same order.
        await engine.HandleIncomingMessageAsync(Phone, "1 = 2", default);
        Assert.Equal(2, (await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id)).Items.Single().Quantity);
    }

    [Fact]
    public async Task ASharedCustomer_AnotherCommandInsteadOfAnAnswer_RunsAndTheChangeIsNotApplied()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var id = await SeedSaraAsync(db, orderCount: 2);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"edit order {id}", default);
        await engine.HandleIncomingMessageAsync(Phone, "name Sara Khan", default);
        await engine.HandleIncomingMessageAsync(Phone, "done", default);

        Assert.Equal("Sara", (await db.Customers.SingleAsync(c => c.Name == "Sara")).Name);
    }

    public void Dispose() => _dbFactory.Dispose();
}
