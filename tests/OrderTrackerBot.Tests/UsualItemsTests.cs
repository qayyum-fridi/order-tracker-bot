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

public class UsualItemsTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();
    private readonly List<(string Body, string[] Labels)> _buttons = new();
    private Seller _seller = null!;
    private Customer _sara = null!;
    private readonly Dictionary<string, Product> _p = new();
    private int _day;

    public UsualItemsTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, l, _) => _buttons.Add((b, l.ToArray()))).Returns(Task.CompletedTask);
    }

    private async Task<(ConversationEngine Engine, AppDbContext Db)> SetupAsync(bool buttons = true)
    {
        var db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(_seller);
        await db.SaveChangesAsync();
        _sara = new Customer { SellerId = _seller.Id, Name = "Sara", Phone = "03001112222", Address = "Model Town" };
        db.Customers.Add(_sara);
        foreach (var (name, price) in new[] { ("Kurti", 1800m), ("Lawn Suit", 3500m), ("Dupatta", 600m), ("Shawl", 2500m) })
        {
            var product = new Product { SellerId = _seller.Id, Name = name, Price = price };
            _p[name] = product;
            db.Products.Add(product);
        }
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.FromResult(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true })),
            _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = buttons });
        return (engine, db);
    }

    private async Task OrderAsync(AppDbContext db, OrderStatus status, params string[] products)
    {
        var order = new Order { SellerId = _seller.Id, CustomerId = _sara.Id, Status = status, CreatedAt = new DateTime(2026, 10, 1).AddDays(_day++) };
        foreach (var name in products)
            order.Items.Add(new OrderItem { ProductId = _p[name].Id, ProductNameSnapshot = name, UnitPrice = _p[name].Price, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task MostFrequentInLastThreeOrders_AreOffered_WithPrices()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, "Kurti", "Lawn Suit");
        await OrderAsync(db, OrderStatus.Delivered, "Kurti", "Lawn Suit", "Dupatta");
        await OrderAsync(db, OrderStatus.Pending, "Kurti");

        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);

        var offer = Assert.Single(_buttons);
        Assert.Equal(new[] { "Kurti Rs.1800", "Lawn Suit Rs.3500", "Dupatta Rs.600" }, offer.Labels);
        Assert.Contains("Sara ke liye order", offer.Body);
    }

    [Fact]
    public async Task OnlyTheLastThreeOrders_AndNeverCancelledOrReturned()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, "Shawl", "Shawl");     // 4th most recent: out of the window
        await OrderAsync(db, OrderStatus.Cancelled, "Dupatta");             // cancelled: ignored
        await OrderAsync(db, OrderStatus.Returned, "Dupatta");              // returned: ignored
        await OrderAsync(db, OrderStatus.Delivered, "Kurti");
        await OrderAsync(db, OrderStatus.Delivered, "Lawn Suit");
        await OrderAsync(db, OrderStatus.Delivered, "Kurti");

        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);

        Assert.Equal(new[] { "Kurti Rs.1800", "Lawn Suit Rs.3500" }, Assert.Single(_buttons).Labels);
    }

    [Fact]
    public async Task Tie_GoesToTheMoreRecentOrder()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, "Dupatta");
        await OrderAsync(db, OrderStatus.Delivered, "Kurti");
        await OrderAsync(db, OrderStatus.Delivered, "Lawn Suit");

        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);

        Assert.Equal(new[] { "Lawn Suit Rs.3500", "Kurti Rs.1800", "Dupatta Rs.600" }, Assert.Single(_buttons).Labels);
    }

    [Fact]
    public async Task SwitchedOffProduct_IsNotOffered()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, "Kurti", "Shawl");
        _p["Shawl"].IsActive = false;
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);

        Assert.Equal(new[] { "Kurti Rs.1800" }, Assert.Single(_buttons).Labels);
    }

    [Fact]
    public async Task NoHistory_KeepsThePlainPrompt()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;

        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);

        Assert.Empty(_buttons);
        Assert.Contains(_text, t => t.Contains("Sara ke liye order"));
    }

    [Fact]
    public async Task ButtonsOff_KeepsThePlainPrompt()
    {
        var (engine, db) = await SetupAsync(buttons: false);
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, "Kurti");

        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);

        Assert.Empty(_buttons);
        Assert.Contains(_text, t => t.Contains("Sara ke liye order"));
    }

    [Fact]
    public async Task Tap_AddsOnePiece_ToSarasDraft_ReadyToConfirm()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, "Lawn Suit");
        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);

        await engine.HandleIncomingMessageAsync(Phone, "Lawn Suit Rs.3500", default);

        var session = (await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync()).Session!;
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, session.State);
        var draft = _text.Last();
        Assert.Contains("Sara", draft);
        Assert.Contains("Lawn Suit", draft);
        Assert.Contains("3,500", draft);
        Assert.Single(await db.Orders.ToListAsync()); // nothing saved until she confirms
    }

    [Fact]
    public async Task SecondTapOfTheSameButton_IsStale_AndChangesNothing()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        await OrderAsync(db, OrderStatus.Delivered, "Lawn Suit");
        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);
        await engine.HandleIncomingMessageAsync(Phone, "Lawn Suit Rs.3500", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var orders = await db.Orders.CountAsync();

        await engine.HandleIncomingMessageAsync(Phone, "Lawn Suit Rs.3500", default);

        Assert.Equal(orders, await db.Orders.CountAsync());
        Assert.Contains(_text, t => t.Contains("purane sawal ka tha"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
