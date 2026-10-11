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

// While a saved order is open for editing, the words that are not edit instructions must neither be lost in the help text nor leave edit mode by accident.
public class EditModeTrapTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public EditModeTrapTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task OnboardAsync(ConversationEngine engine)
    {
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
    }

    // A pending order of Sara's: one line per (price, quantity) pair.
    private async Task<int> SeedOrderAsync(AppDbContext db, params decimal[] itemPrices)
    {
        var seller = await db.Sellers.FirstAsync();
        var customer = db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" }).Entity;
        await db.SaveChangesAsync();
        var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Status = OrderStatus.Pending };
        foreach (var price in itemPrices) order.Items.Add(new OrderItem { ProductNameSnapshot = "Kurti", UnitPrice = price, Quantity = 1 });
        order.Subtotal = itemPrices.Sum();
        order.Total = itemPrices.Sum();
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<(ConversationEngine Engine, int OrderId)> OpenEditAsync(AppDbContext db, params decimal[] itemPrices)
    {
        var engine = CreateEngine(db);
        await OnboardAsync(engine);
        var id = await SeedOrderAsync(db, itemPrices);
        await engine.HandleIncomingMessageAsync(Phone, $"edit order {id}", default);
        _sent.Clear();
        return (engine, id);
    }

    private static async Task<ConversationState> StateAsync(AppDbContext db) => (await db.Sessions.FirstAsync()).State;

    [Theory]
    [InlineData("ok")]
    [InlineData("theek hai")]
    [InlineData("shukriya")]
    [InlineData("Achha ji")]
    [InlineData("Theek ae")]
    [InlineData("Jee bilkul")]
    [InlineData("Changa ji")]
    public async Task Acknowledgement_StaysInEditMode_AndSaysHowToFinish(string reply)
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, reply, default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("Edit jaari hai") && m.Contains("done"));
        Assert.DoesNotContain(_sent, m => m.Contains("Samajh nahi aaya"));
        var order = await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(1, order.Items.Single().Quantity);
    }

    [Fact]
    public async Task Haan_WhileEditing_DoesNotPlaceTheOrder()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, "haan", default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Equal(OrderStatus.Pending, (await db.Orders.AsNoTracking().FirstAsync(o => o.Id == id)).Status);
        Assert.Contains(_sent, m => m.Contains("save nahi hoga") && m.Contains("done"));
    }

    [Fact]
    public async Task Nahi_AsksKeepOrDiscard_AndDoesNotAssume()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, _) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, "nahi", default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("\"jaari\"") && m.Contains("\"chhoro\""));
    }

    [Fact]
    public async Task JaariRakho_AfterNahi_KeepsEditing()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, _) = await OpenEditAsync(db, 2000m);
        await engine.HandleIncomingMessageAsync(Phone, "nahi", default);

        await engine.HandleIncomingMessageAsync(Phone, "jaari rakho", default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("Edit jaari hai"));
    }

    [Fact]
    public async Task Nahi_ThenJaari_KeepsEditing()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, _) = await OpenEditAsync(db, 2000m);
        await engine.HandleIncomingMessageAsync(Phone, "nahi", default);

        await engine.HandleIncomingMessageAsync(Phone, "jaari", default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("Edit jaari hai"));
    }

    [Fact]
    public async Task Nahi_ThenChhoro_DiscardsTheEditsMadeInThisSession()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m);
        await engine.HandleIncomingMessageAsync(Phone, "1 = 3", default);
        Assert.Equal(3, (await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id)).Items.Single().Quantity);
        await engine.HandleIncomingMessageAsync(Phone, "nahi", default);

        await engine.HandleIncomingMessageAsync(Phone, "chhoro", default);

        Assert.Equal(ConversationState.Idle, await StateAsync(db));
        var order = await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id);
        Assert.Equal(1, order.Items.Single().Quantity);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Contains(_sent, m => m.Contains("Order cancel nahi hua"));
    }

    [Fact]
    public async Task Done_SavesAndLeavesEditMode()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, _) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, "done", default);

        Assert.Equal(ConversationState.Idle, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("save ho gaya"));
    }

    [Theory]
    [InlineData("cancel karo")]
    [InlineData("Nahi bhai, cancel karo")]
    public async Task CancelInstruction_DiscardsTheEdit_NotTheOrder(string reply)
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, reply, default);

        Assert.Equal(ConversationState.Idle, await StateAsync(db));
        Assert.Equal(OrderStatus.Pending, (await db.Orders.AsNoTracking().FirstAsync(o => o.Id == id)).Status);
        Assert.Contains(_sent, m => m.Contains("Order cancel nahi hua") && m.Contains($"cancel order {id}"));
    }

    [Theory]
    [InlineData("cancel mat karo")]
    [InlineData("cancel nahi karna")]
    public async Task CancelWithANegation_KeepsTheDraftOpen(string reply)
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, reply, default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Equal(OrderStatus.Pending, (await db.Orders.AsNoTracking().FirstAsync(o => o.Id == id)).Status);
        Assert.Contains(_sent, m => m.Contains("cancel nahi kiya"));
    }

    [Fact]
    public async Task Correction_AppliesWhenExactlyOneFieldHoldsTheOldAmount()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, "2000 nahi, 1800", default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Equal(1800m, (await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id)).Items.Single().UnitPrice);
    }

    [Fact]
    public async Task Correction_IsNotGuessed_WhenTwoFieldsHoldTheOldAmount()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, "2000 nahi, 1800", default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("Kaunsi cheez badlni hai"));
        Assert.All((await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id)).Items, i => Assert.Equal(2000m, i.UnitPrice));
    }

    [Fact]
    public async Task Correction_WithNoMatchingField_ChangesNothing()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, "3500 nahi, 5300", default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("kahin nahi milta"));
        Assert.Equal(2000m, (await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id)).Items.Single().UnitPrice);
    }

    [Theory]
    [InlineData("2000 nahi, 1800 delivery")]
    [InlineData("2000 nahi, 1800 discount")]
    [InlineData("rate 2000 nahi, 1800 delivery samet")]
    public async Task Correction_WhoseWordsNameAnotherField_AsksAndChangesNothing(string said)
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, said, default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("Kaunsi cheez badlni hai"));
        Assert.Equal(2000m, (await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id)).Items.Single().UnitPrice);
    }

    [Fact]
    public async Task Correction_NamingTheSameField_StillApplies()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, id) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, "rate 2000 nahi, 1800", default);

        Assert.Equal(1800m, (await db.Orders.Include(o => o.Items).AsNoTracking().FirstAsync(o => o.Id == id)).Items.Single().UnitPrice);
    }

    [Theory]
    [InlineData("kya karun")]
    [InlineData("?")]
    public async Task Help_ShowsTheEditOptions_AndChangesNothing(string reply)
    {
        using var db = _dbFactory.CreateContext();
        var (engine, _) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, reply, default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("Kya badalna hai"));
    }

    [Fact]
    public async Task UnknownText_StillSaysSamajhNahiAaya_AndStaysInEditMode()
    {
        using var db = _dbFactory.CreateContext();
        var (engine, _) = await OpenEditAsync(db, 2000m);

        await engine.HandleIncomingMessageAsync(Phone, "kuch bhi", default);

        Assert.Equal(ConversationState.AwaitingOrderEdit, await StateAsync(db));
        Assert.Contains(_sent, m => m.Contains("Samajh nahi aaya") && m.Contains("done"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
