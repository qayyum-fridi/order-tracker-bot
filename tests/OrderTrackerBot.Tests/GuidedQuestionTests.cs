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

// The model understood the job, but a detail is missing or fits several records: the bot asks with taps (ConversationEngine.Guided.cs),
// a tap fills the detail, and the usual YES still guards the change. The model's answers are scripted, so no key is needed.
public class GuidedQuestionTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    private readonly List<string> _rowIds = new();
    private readonly Dictionary<string, TurnAction> _model = new();
    private int _saraId, _bilalId, _aliRazaId, _aliHassanId;

    public void Dispose() => _dbFactory.Dispose();

    private async Task<(AppDbContext Db, Func<string, Task> Say)> ShopAsync()
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", BusinessType = "Clothing", OnboardingComplete = true,
            Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var shirt = new Product { SellerId = seller.Id, Name = "Shirt", Price = 1800 };
        db.Products.Add(shirt);
        var customers = new[]
        {
            new Customer { SellerId = seller.Id, Name = "Sara Khan", Phone = "03004445566" },
            new Customer { SellerId = seller.Id, Name = "Bilal Ahmed", Phone = "03215557788" },
            new Customer { SellerId = seller.Id, Name = "Ali Raza", Phone = "03331112233" },
            new Customer { SellerId = seller.Id, Name = "Ali Hassan", Phone = "03451234567" },
        };
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();
        (_saraId, _bilalId, _aliRazaId, _aliHassanId) = (customers[0].Id, customers[1].Id, customers[2].Id, customers[3].Id);
        // #1 Sara, #2 Bilal, #3 Ali Raza, #4 Ali Hassan: all pending, oldest first.
        for (var i = 0; i < customers.Length; i++)
        {
            var order = new Order { SellerId = seller.Id, CustomerId = customers[i].Id, CreatedAt = DateTime.UtcNow.AddHours(-10 + i), Subtotal = 1800, Total = 1800 };
            order.Items.Add(new OrderItem { ProductId = shirt.Id, ProductNameSnapshot = "Shirt", UnitPrice = 1800, Quantity = 1 });
            db.Orders.Add(order);
        }
        await db.SaveChangesAsync();

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

    private static async Task<OrderStatus> StatusOf(AppDbContext db, int customerId) =>
        (await db.Orders.AsNoTracking().SingleAsync(o => o.CustomerId == customerId)).Status;

    private static async Task<int> OrderOf(AppDbContext db, int customerId) =>
        (await db.Orders.AsNoTracking().SingleAsync(o => o.CustomerId == customerId)).Id;

    [Fact]
    public async Task CancelByName_WithOneOpenOrder_UsesThatOrder_AndStillAsksYes()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Sara ka order zara khatam karo"] = new TurnAction { Action = TurnAction.CancelOrder, CustomerName = "Sara" };

        await say("Sara ka order zara khatam karo");
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _saraId));   // nothing changes before YES
        Assert.DoesNotContain(_sent, m => m.Contains("samajh nahi aayi"));

        await say("yes");
        Assert.Equal(OrderStatus.Cancelled, await StatusOf(db, _saraId));
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _bilalId));
    }

    [Fact]
    public async Task StatusForANameThatFitsTwoCustomers_ListsTheirOrders_AndTheTappedOneChanges()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Ali wala parcel courier ko de diya"] = new TurnAction { Action = TurnAction.UpdateStatus, CustomerName = "Ali", Status = "shipped" };
        var raza = await OrderOf(db, _aliRazaId);
        var hassan = await OrderOf(db, _aliHassanId);
        var sara = await OrderOf(db, _saraId);

        await say("Ali wala parcel courier ko de diya");
        Assert.Contains(_sent, m => m.StartsWith("❓ Kis order ko"));
        Assert.Contains($"#{raza}", _rowIds);
        Assert.Contains($"#{hassan}", _rowIds);
        Assert.DoesNotContain($"#{sara}", _rowIds);

        await say($"#{raza}");   // the tapped row
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _aliRazaId));
        await say("yes");

        Assert.Equal(OrderStatus.Shipped, await StatusOf(db, _aliRazaId));
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _aliHassanId));
    }

    [Fact]
    public async Task CancelWithNoOrderOrName_ListsRecentOrders_AndANumberPicksOne()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["ek order cancel karna hai"] = new TurnAction { Action = TurnAction.CancelOrder };

        await say("ek order cancel karna hai");
        Assert.Contains(_sent, m => m.StartsWith(MessageTemplates.Get("guided.order.cancel")));
        Assert.Equal(4, _rowIds.Count(id => id.StartsWith('#')));   // newest first: Ali Hassan, Ali Raza, Bilal, Sara

        await say("2");
        await say("yes");

        Assert.Equal(OrderStatus.Cancelled, await StatusOf(db, _aliRazaId));
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _aliHassanId));
    }

    [Fact]
    public async Task EditingACustomerNamedAmbiguously_AsksWhichOne_AndOnlyThatOneChanges()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Ali ka naya number 03001112222 hai"] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Ali", Field = "phone", Value = "03001112222" };

        await say("Ali ka naya number 03001112222 hai");
        Assert.Contains(_sent, m => m.Contains("naam ke 2 customer hain"));
        Assert.Contains($"@{_aliHassanId}", _rowIds);

        await say($"@{_aliHassanId}");
        await say("yes");

        Assert.Equal("03001112222", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _aliHassanId)).Phone);
        Assert.Equal("03331112233", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _aliRazaId)).Phone);
    }

    [Fact]
    public async Task AMissingValue_GetsOneSpecificQuestion_AndNothingChanges()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Bilal ka number badalna hai"] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Bilal", Field = "phone" };
        _model["Shirt ki qeemat badalni hai"] = new TurnAction { Action = TurnAction.EditProduct, Products = { new AiNewProduct { Name = "Shirt" } } };

        await say("Bilal ka number badalna hai");
        await say("Shirt ki qeemat badalni hai");

        Assert.Contains(MessageTemplates.Get("guided.customer.value", "Bilal Ahmed", "phone", "03001234567"), _sent);
        Assert.Contains(MessageTemplates.Get("guided.product.price", "Shirt"), _sent);
        Assert.Equal("03215557788", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _bilalId)).Phone);
        Assert.Equal(1800, (await db.Products.AsNoTracking().SingleAsync()).Price);
    }

    private static async Task<bool> ShirtActive(AppDbContext db) => (await db.Products.AsNoTracking().SingleAsync()).IsActive;

    [Fact]
    public async Task DeletingAProductInOwnWords_AsksYesFirst_ThenTakesItOffTheList()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Shirt ab nahi bechti, list se hata do"] = new TurnAction { Action = TurnAction.DeleteProduct, Products = { new AiNewProduct { Name = "Shirt" } } };

        await say("Shirt ab nahi bechti, list se hata do");
        Assert.Contains(MessageTemplates.Get("model-delete.001", "Shirt"), _sent);
        Assert.True(await ShirtActive(db));

        await say("yes");
        Assert.False(await ShirtActive(db));
        Assert.Single(await db.Orders.AsNoTracking().Where(o => o.Items.Any(i => i.ProductNameSnapshot == "Shirt")).Select(o => o.Id).Take(1).ToListAsync());
    }

    [Fact]
    public async Task DeletingAProduct_AndSayingNo_KeepsIt()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["shirt band kar do"] = new TurnAction { Action = TurnAction.DeleteProduct, Products = { new AiNewProduct { Name = "Shirt" } } };

        await say("shirt band kar do");
        await say("no");

        Assert.True(await ShirtActive(db));
    }

    [Fact]
    public async Task DeletingAProductWithNoName_ListsProductsToTap()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["ek product hatana hai"] = new TurnAction { Action = TurnAction.DeleteProduct };
        var shirtId = (await db.Products.AsNoTracking().SingleAsync()).Id;

        await say("ek product hatana hai");
        Assert.Contains(_sent, m => m.StartsWith(MessageTemplates.Get("guided.product.delete")));
        Assert.Contains($"%{shirtId}", _rowIds);

        await say($"%{shirtId}");
        Assert.True(await ShirtActive(db));
        await say("yes");
        Assert.False(await ShirtActive(db));
    }

    [Fact]
    public async Task DeletingACustomerInOwnWords_AsksYes_ThenRemovesOnlyThem()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Bilal ab nahi aata, usko list se nikal do"] = new TurnAction { Action = TurnAction.DeleteCustomer, CustomerName = "Bilal" };

        await say("Bilal ab nahi aata, usko list se nikal do");
        Assert.Null((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _bilalId)).DeletedAt);
        await say("yes");

        Assert.NotNull((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _bilalId)).DeletedAt);
        Assert.Null((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _saraId)).DeletedAt);
    }

    [Fact]
    public async Task DeletingACustomerNamedAmbiguously_AsksWhichOne_AndOnlyThatOneGoes()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Ali ko list se hata do"] = new TurnAction { Action = TurnAction.DeleteCustomer, CustomerName = "Ali" };

        await say("Ali ko list se hata do");
        Assert.Contains(_sent, m => m.Contains("naam ke 2 customer hain"));

        await say($"@{_aliRazaId}");
        await say("yes");

        Assert.NotNull((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _aliRazaId)).DeletedAt);
        Assert.Null((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _aliHassanId)).DeletedAt);
    }

    // ----- Restoring by voice or in own words -----

    [Fact]
    public async Task RestoringARemovedProduct_AsksYes_ThenPutsItBack()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        (await db.Products.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        _model["Shirt wapas list mein daal do please"] = new TurnAction { Action = TurnAction.RestoreProduct, Products = { new AiNewProduct { Name = "Shirt" } } };

        await say("Shirt wapas list mein daal do please");
        Assert.Contains(MessageTemplates.Get("model-restore.001", "Shirt"), _sent);
        Assert.False(await ShirtActive(db));

        await say("yes");
        Assert.True(await ShirtActive(db));
        Assert.Contains(MessageTemplates.Get("model-restore.002", "Shirt"), _sent);
    }

    [Fact]
    public async Task RestoringAProductThatIsStillOnTheList_SaysSo_AndChangesNothing()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Shirt wapas list mein daal do please"] = new TurnAction { Action = TurnAction.RestoreProduct, Products = { new AiNewProduct { Name = "Shirt" } } };

        await say("Shirt wapas list mein daal do please");

        Assert.Contains(MessageTemplates.Get("model-restore.004", "Shirt"), _sent);
        Assert.Equal(ConversationState.Idle, (await db.Sessions.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task RestoringARemovedCustomer_BringsThemBack()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        (await db.Customers.SingleAsync(c => c.Id == _bilalId)).DeletedAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        _model["Bilal ko wapas le aao list mein"] = new TurnAction { Action = TurnAction.RestoreCustomer, CustomerName = "Bilal" };

        await say("Bilal ko wapas le aao list mein");

        Assert.Null((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _bilalId)).DeletedAt);
    }

    // ----- Several requests in one message -----

    [Fact]
    public async Task TwoRequests_TheFirstMissingADetail_IsAskedFirst_AndTheSecondRunsAfter()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        const string message = "ek order cancel karna hai aur Bilal ka number 03001112222 kar do";
        _model[message] = new TurnAction
        {
            Action = TurnAction.CancelOrder,
            Also = { new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Bilal", Field = "phone", Value = "03001112222" } },
        };
        var sara = await OrderOf(db, _saraId);

        await say(message);
        Assert.Contains(_sent, m => m.StartsWith(MessageTemplates.Get("guided.order.cancel")));   // the first request asks which order
        Assert.Equal("03215557788", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _bilalId)).Phone);   // the second waits

        await say($"#{sara}");
        await say("yes");   // the cancel
        Assert.Equal(OrderStatus.Cancelled, await StatusOf(db, _saraId));

        await say("yes");   // the phone change, asked once the cancel was answered
        Assert.Equal("03001112222", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _bilalId)).Phone);
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _bilalId));
    }

    [Fact]
    public async Task TwoRequests_TheSecondFitsTwoCustomers_IsAskedOnceTheFirstIsDone()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        const string message = "Shirt ka rate 2000 karo aur Ali ko list se hata do";
        _model[message] = new TurnAction
        {
            Action = TurnAction.EditProduct, Products = { new AiNewProduct { Name = "Shirt", Price = 2000 } },
            Also = { new TurnAction { Action = TurnAction.DeleteCustomer, CustomerName = "Ali" } },
        };

        await say(message);
        Assert.DoesNotContain(_sent, m => m.Contains("naam ke 2 customer hain"));   // not yet: the price change asks first
        await say("yes");
        Assert.Equal(2000, (await db.Products.AsNoTracking().SingleAsync()).Price);
        Assert.Contains(_sent, m => m.Contains("naam ke 2 customer hain"));

        await say($"@{_aliHassanId}");
        await say("yes");
        Assert.NotNull((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _aliHassanId)).DeletedAt);
        Assert.Null((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _aliRazaId)).DeletedAt);
    }

    [Fact]
    public async Task AnotherMessage_InsteadOfATap_IsHandledOnItsOwn_AndChangesNothing()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["ek order cancel karna hai"] = new TurnAction { Action = TurnAction.CancelOrder };

        await say("ek order cancel karna hai");
        _sent.Clear();
        await say("orders today");

        Assert.Equal(ConversationState.Idle, (await db.Sessions.AsNoTracking().SingleAsync()).State);
        Assert.All(await db.Orders.AsNoTracking().ToListAsync(), o => Assert.Equal(OrderStatus.Pending, o.Status));
        Assert.NotEmpty(_sent);
    }
}
