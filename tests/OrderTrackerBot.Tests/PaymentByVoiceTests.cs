using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Money received, said in own words ("Sara ne 2000 diye", "Bilal ka poora paisa aa gaya"): the order is found, YES is asked, then the typed payment path runs.
// The model's answers are scripted, so no key is needed.
public class PaymentByVoiceTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    private readonly List<string> _rowIds = new();
    private readonly Dictionary<string, TurnAction> _model = new();
    private readonly Dictionary<string, int> _orderOf = new();

    public void Dispose() => _dbFactory.Dispose();

    private async Task<(AppDbContext Db, Func<string, Task> Say)> ShopAsync()
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", BusinessType = "Clothing", OnboardingComplete = true,
            Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var suit = new Product { SellerId = seller.Id, Name = "Lawn Suit", Price = 5000 };
        db.Products.Add(suit);
        var names = new[] { "Sara Khan", "Bilal Ahmed", "Ali Raza", "Hina Raza" };
        var customers = names.Select(n => new Customer { SellerId = seller.Id, Name = n, Phone = "0300" + (1000000 + n.Length) }).ToList();
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();
        for (var i = 0; i < customers.Count; i++)
        {
            var order = new Order { SellerId = seller.Id, CustomerId = customers[i].Id, CreatedAt = DateTime.UtcNow.AddHours(-10 + i), Subtotal = 5000, Total = 5000 };
            order.Items.Add(new OrderItem { ProductId = suit.Id, ProductNameSnapshot = "Lawn Suit", UnitPrice = 5000, Quantity = 1 });
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            _orderOf[names[i].Split(' ')[0]] = order.Id;
        }

        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _sent.Add(b)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, sections, _) =>
            {
                _sent.Add(body);
                _rowIds.Clear();
                _rowIds.AddRange(sections.SelectMany(s => s.Rows).Select(r => r.Id));
            })
            .Returns(Task.CompletedTask);
        var older = new Mock<IAiOrderAssistant>();
        older.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
        var turn = new Mock<ITurnInterpreter>();
        turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), It.IsAny<string>(), It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TurnContext _, string text, AiImageInput? _, CancellationToken _) => _model.GetValueOrDefault(text));
        var features = new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, SuggestionButtons = false };

        return (db, async message =>
        {
            var engine = new ConversationEngine(db, older.Object, sender.Object, Mock.Of<IFounderAlertNotifier>(), features: features, turn: turn.Object);
            await engine.HandleIncomingMessageAsync(Phone, message, default);
        });
    }

    private async Task<Order> OrderOf(AppDbContext db, string firstName) =>
        await db.Orders.AsNoTracking().SingleAsync(o => o.Id == _orderOf[firstName]);

    [Fact]
    public async Task PartPayment_ByName_AsksYesWithTheBalance_ThenRecordsIt()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Sara ne 2000 diye"] = new TurnAction { Action = TurnAction.RecordPayment, CustomerName = "Sara", Amount = 2000 };

        await say("Sara ne 2000 diye");
        Assert.Contains(_sent, m => m.Contains($"Order #{_orderOf["Sara"]}") && m.Contains("2,000") && m.Contains("3,000"));
        Assert.Equal(0, (await OrderOf(db, "Sara")).AmountPaid);   // nothing before YES

        await say("yes");
        var order = await OrderOf(db, "Sara");
        Assert.Equal(2000, order.AmountPaid);
        Assert.NotEqual(PaymentStatus.Paid, order.PaymentStatus);
        Assert.Equal(0, (await OrderOf(db, "Bilal")).AmountPaid);
    }

    [Fact]
    public async Task PaidInFull_MarksTheOrderPaid_AfterYes()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Bilal ka poora paisa aa gaya"] = new TurnAction { Action = TurnAction.RecordPayment, CustomerName = "Bilal", PaidInFull = true };

        await say("Bilal ka poora paisa aa gaya");
        Assert.Contains(_sent, m => m.Contains($"Order #{_orderOf["Bilal"]}") && m.Contains("poora paid"));
        await say("yes");

        Assert.Equal(PaymentStatus.Paid, (await OrderOf(db, "Bilal")).PaymentStatus);
    }

    [Fact]
    public async Task AnAmountTheSellerNeverSaid_IsNotUsed_TheBotAsksHowMuch()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Sara ne paise de diye hain"] = new TurnAction { Action = TurnAction.RecordPayment, CustomerName = "Sara", Amount = 3000 };

        await say("Sara ne paise de diye hain");

        Assert.Contains(_sent, m => m.Contains("ne kitne paise diye"));
        Assert.Equal(0, (await OrderOf(db, "Sara")).AmountPaid);
        Assert.Equal(ConversationState.Idle, (await db.Sessions.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task MoneyWithNoOrderOrName_ListsUnpaidOrders_AndTheTappedOneIsPaid()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["COD ke 5000 aa gaye"] = new TurnAction { Action = TurnAction.RecordPayment, Amount = 5000 };

        await say("COD ke 5000 aa gaye");
        Assert.Contains(_sent, m => m.StartsWith(MessageTemplates.Get("guided.order.pay")));
        Assert.Contains($"#{_orderOf["Ali"]}", _rowIds);

        await say($"#{_orderOf["Ali"]}");
        await say("yes");

        Assert.Equal(PaymentStatus.Paid, (await OrderOf(db, "Ali")).PaymentStatus);
        Assert.Equal(PaymentStatus.Unpaid, (await OrderOf(db, "Hina")).PaymentStatus);
    }

    [Fact]
    public async Task AConfirmedOrderNumber_IsNeverReadAsARowOfAnEarlierList()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        // The seller saw a list earlier: row 2 is Hina's order, which is not order #2.
        var session = await db.Sessions.SingleAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.LastListOrderIds = new List<int> { _orderOf["Ali"], _orderOf["Hina"], _orderOf["Bilal"], _orderOf["Sara"] };
        session.ContextJson = ctx.ToJson();
        await db.SaveChangesAsync();
        Assert.Equal(2, _orderOf["Bilal"]);
        // A free sentence (not the typed "order 2 bhej diya", where 2 is a list row on purpose): the model's 2 is order #2.
        const string spoken = "order number 2 wala parcel courier ko de diya hai";
        _model[spoken] = new TurnAction { Action = TurnAction.UpdateStatus, OrderId = 2, Status = "shipped" };

        await say(spoken);
        await say("yes");

        Assert.Equal(OrderStatus.Shipped, (await OrderOf(db, "Bilal")).Status);
        Assert.Equal(OrderStatus.Pending, (await OrderOf(db, "Hina")).Status);
    }
}
