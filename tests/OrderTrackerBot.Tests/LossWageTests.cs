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

public class LossWageParserTests
{
    [Theory]
    [InlineData("loss 2 Kurti damaged", 2, "Kurti", "damaged")]
    [InlineData("nuqsan 2 Kurti", 2, "Kurti", null)]
    [InlineData("damage 1 Lawn Suit expired", 1, "Lawn Suit", "expired")]
    public void ParsesLoss(string message, int quantity, string name, string? reason)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.Loss, parsed!.Kind);
        Assert.Equal((decimal)quantity, parsed.Amount);
        Assert.Equal(name, parsed.Text);
        Assert.Equal(reason, parsed.Text2);
    }

    [Theory]
    [InlineData("Ali ki 5000 dihari", "Ali", 5000, 1)]
    [InlineData("Ali ko 3 din 1500", "Ali", 4500, 3)]
    [InlineData("wage Ali 5000", "Ali", 5000, null)]
    [InlineData("salary Ayesha Khan 12,000", "Ayesha Khan", 12000, null)]
    public void ParsesWage(string message, string worker, double amount, int? days)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.Equal(CommandKind.Wage, parsed!.Kind);
        Assert.Equal(worker, parsed.Text);
        Assert.Equal((decimal)amount, parsed.Amount);
        Assert.Equal(days, parsed.Number);
    }

    [Theory]
    [InlineData("Sara ka phone 03001234567")]
    [InlineData("Bilal ki city Lahore")]
    [InlineData("expense 500 packaging")]
    [InlineData("loss")]
    public void OtherMessagesAreNotLossOrWage(string message)
    {
        var kind = CommandParser.TryParse(message)?.Kind;
        Assert.NotEqual(CommandKind.Loss, kind);
        Assert.NotEqual(CommandKind.Wage, kind);
    }
}

public class LossWageEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public LossWageEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task<Seller> OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
        return await db.Sellers.FirstAsync();
    }

    private static async Task SetKurtiAsync(AppDbContext db, int? stock, decimal? cost)
    {
        var kurti = await db.Products.FirstAsync(p => p.Name == "Kurti");
        kurti.StockQty = stock;
        kurti.CostPrice = cost;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Loss_WritesStockOffAtCost_AndUndoRestoresBoth()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        await SetKurtiAsync(db, stock: 10, cost: 800);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "loss 2 Kurti damaged", default);

        var loss = await db.Losses.SingleAsync();
        Assert.Equal(2, loss.Quantity);
        Assert.Equal(800m, loss.UnitCost);
        Assert.Equal("damaged", loss.Reason);
        Assert.Equal(8, (await db.Products.FirstAsync(p => p.Name == "Kurti")).StockQty);
        Assert.Contains(_sent, m => m.Contains("Cost: Rs.1,600"));

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);

        Assert.Empty(db.Losses);
        Assert.Equal(10, (await db.Products.FirstAsync(p => p.Name == "Kurti")).StockQty);
    }

    [Fact]
    public async Task Loss_OfUnknownProduct_IsRecordedAtZeroCost_WithoutStockChange()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "loss 1 Mystery Bag", default);

        var loss = await db.Losses.SingleAsync();
        Assert.Null(loss.ProductId);
        Assert.Equal("Mystery Bag", loss.ProductName);
        Assert.Equal(0m, loss.UnitCost);
        Assert.Contains(_sent, m => m.Contains("catalog mein nahi"));
    }

    [Fact]
    public async Task Wage_IsSaved_AndUndoRemovesIt()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "Ali ki 5000 dihari", default);

        var wage = await db.WageEntries.SingleAsync();
        Assert.Equal("Ali", wage.WorkerName);
        Assert.Equal(5000m, wage.Amount);
        Assert.Equal(1, wage.Days);
        Assert.Contains(_sent, m => m.Contains("Ali ki tankhwah save: Rs.5,000"));

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);

        Assert.Empty(db.WageEntries);
    }

    [Fact]
    public async Task MonthlyNet_SubtractsWages_AndShowsLossesAtCostOnTheirOwnLine()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await OnboardAsync(db);
        await SetKurtiAsync(db, stock: null, cost: 800);
        var customer = db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" }).Entity;
        await db.SaveChangesAsync();
        db.Orders.Add(new Order { SellerId = seller.Id, CustomerId = customer.Id, Status = OrderStatus.Delivered, Subtotal = 1000, Total = 1000 });
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "expense 200 packaging", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ali ki 300 dihari", default);
        await engine.HandleIncomingMessageAsync(Phone, "loss 1 Kurti", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "monthly net", default);

        var report = Assert.Single(_sent);
        Assert.Contains("Tankhwah: Rs.300 (1)", report);
        Assert.Contains("Nuqsan (cost, net mein shamil nahi): Rs.800 (1)", report);
        Assert.Contains("Net: Rs.500", report);
    }

    public void Dispose() => _dbFactory.Dispose();
}
