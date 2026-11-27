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

// Stage 6 in the engine: a guided question is saved as a PendingActions row; the reply is checked against it; an expired one runs nothing.
public class GuidedPendingActionTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    private readonly Dictionary<string, TurnAction> _model = new();
    private readonly ManualClock _clock = new();
    private int _saraId, _bilalId;

    public void Dispose() => _dbFactory.Dispose();

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(AppDbContext Db, Func<string, Task> Say)> ShopAsync()
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", BusinessType = "Clothing", OnboardingComplete = true,
            Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var shirt = new Product { SellerId = seller.Id, Name = "Shirt", Price = 1800 };
        db.Products.Add(shirt);
        var sara = new Customer { SellerId = seller.Id, Name = "Sara Khan" };
        var bilal = new Customer { SellerId = seller.Id, Name = "Bilal Ahmed" };
        db.Customers.AddRange(sara, bilal);
        await db.SaveChangesAsync();
        (_saraId, _bilalId) = (sara.Id, bilal.Id);
        foreach (var (customer, hoursAgo) in new[] { (sara, 10), (bilal, 9) })
        {
            var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, CreatedAt = DateTime.UtcNow.AddHours(-hoursAgo), Subtotal = 1800, Total = 1800 };
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
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        var older = new Mock<IAiOrderAssistant>();
        older.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
        var turn = new Mock<ITurnInterpreter>();
        turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), It.IsAny<string>(), It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TurnContext _, string text, AiImageInput? _, CancellationToken _) => _model.GetValueOrDefault(text));
        var features = new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, SuggestionButtons = false };

        return (db, async message =>
        {
            var engine = new ConversationEngine(db, older.Object, sender.Object, Mock.Of<IFounderAlertNotifier>(), features: features, turn: turn.Object, clock: _clock);
            await engine.HandleIncomingMessageAsync(Phone, message, default);
        });
    }

    private static async Task<OrderStatus> StatusOf(AppDbContext db, int customerId) =>
        (await db.Orders.AsNoTracking().SingleAsync(o => o.CustomerId == customerId)).Status;

    private static Task<List<PendingActionRecord>> Pendings(AppDbContext db) => db.PendingActions.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();

    [Fact]
    public async Task GuidedQuestion_IsSavedAsPendingAction_BoundToSellerAndUser()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["ek order cancel karna hai"] = new TurnAction { Action = TurnAction.CancelOrder };

        await say("ek order cancel karna hai");

        var row = Assert.Single(await Pendings(db));
        Assert.Equal("Waiting", row.Status);
        Assert.Equal(Phone, row.UserId);
        Assert.Equal((await db.Sellers.SingleAsync()).Id, row.SellerId);
        Assert.Equal(TurnAction.CancelOrder, row.Action);
        Assert.Equal(MessageTemplates.Get("guided.order.cancel"), row.CurrentQuestion);
        Assert.True(row.ExpiresAt > row.CreatedAt);
    }

    [Fact]
    public async Task ValidAnswer_CompletesThePending_AndTheActionStillAsksYes()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["ek order cancel karna hai"] = new TurnAction { Action = TurnAction.CancelOrder };

        await say("ek order cancel karna hai");
        await say("2");   // newest first: 1 = Bilal, 2 = Sara

        Assert.Equal("Completed", Assert.Single(await Pendings(db)).Status);
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _saraId));   // the reply did not change the order by itself
        await say("yes");
        Assert.Equal(OrderStatus.Cancelled, await StatusOf(db, _saraId));
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _bilalId));
    }

    [Fact]
    public async Task UnrelatedReply_CancelsThePending_AndIsHandledAsANewMessage()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["ek order cancel karna hai"] = new TurnAction { Action = TurnAction.CancelOrder };

        await say("ek order cancel karna hai");
        await say("orders today");

        Assert.Equal("Cancelled", Assert.Single(await Pendings(db)).Status);
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _saraId));
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _bilalId));
    }

    [Fact]
    public async Task AnswerAfterExpiry_RunsNothing_AndAsksToStartAgain()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["ek order cancel karna hai"] = new TurnAction { Action = TurnAction.CancelOrder };

        await say("ek order cancel karna hai");
        _clock.Now += TimeSpan.FromMinutes(11);
        _sent.Clear();
        await say("2");

        Assert.Contains(MessageTemplates.Get("guided.expired"), _sent);
        Assert.Equal("Expired", Assert.Single(await Pendings(db)).Status);
        await say("yes");
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _saraId));
        Assert.Equal(OrderStatus.Pending, await StatusOf(db, _bilalId));
    }

    [Fact]
    public async Task ANewGuidedQuestion_LeavesOnlyTheNewestWaiting()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["ek order cancel karna hai"] = new TurnAction { Action = TurnAction.CancelOrder };
        _model["koi order cancel kar do"] = new TurnAction { Action = TurnAction.CancelOrder };

        await say("ek order cancel karna hai");
        await say("koi order cancel kar do");

        var rows = await Pendings(db);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Cancelled", rows[0].Status);
        Assert.Equal("Waiting", rows[1].Status);
    }

    [Fact]
    public async Task MissingValueQuestion_IsPending_AndAFittingReplyRunsTheEditAfterItsOwnYes()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["sara ka phone badal do"] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Sara Khan", Field = "phone" };

        await say("sara ka phone badal do");
        var asked = Assert.Single(await Pendings(db));
        Assert.Equal("Waiting", asked.Status);
        Assert.Equal(MessageTemplates.Get("guided.customer.value", "Sara Khan", "phone", "03001234567"), asked.CurrentQuestion);

        await say("03009998877");
        Assert.Equal("Completed", Assert.Single(await Pendings(db)).Status);
        await say("yes");
        Assert.Equal("03009998877", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _saraId)).Phone);
    }

    [Fact]
    public async Task MissingValueQuestion_ReplyThatDoesNotFitTheField_CancelsAndRunsNothing()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["sara ka phone badal do"] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Sara Khan", Field = "phone" };

        await say("sara ka phone badal do");
        _sent.Clear();
        await say("abc");

        // First invalid reply: the same question is asked again and the record keeps waiting.
        var asked = Assert.Single(await Pendings(db));
        Assert.Equal("Waiting", asked.Status);
        Assert.Contains(MessageTemplates.Get("guided.customer.value", "Sara Khan", "phone", "03001234567"), _sent);

        // Second invalid reply reaches the limit: the question closes and runs nothing.
        await say("xyz");
        Assert.Equal("Cancelled", Assert.Single(await Pendings(db)).Status);
        Assert.Null((await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _saraId)).Phone);
    }

    [Fact]
    public async Task WhatToChangeQuestion_ReplyNamingAField_RunsTheEditAfterYes()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["sara ka kuch badlna hai"] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Sara Khan" };

        await say("sara ka kuch badlna hai");
        var asked = Assert.Single(await Pendings(db));
        Assert.Equal(MessageTemplates.Get("guided.customer.what", "Sara Khan"), asked.CurrentQuestion);

        await say("city Lahore");
        Assert.Equal("Completed", Assert.Single(await Pendings(db)).Status);
        await say("yes");
        Assert.Equal("Lahore", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == _saraId)).City);
    }

    [Fact]
    public async Task WhatToChangeQuestion_ReplyWithoutAField_IsAskedAgain()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["sara ka kuch badlna hai"] = new TurnAction { Action = TurnAction.EditCustomer, CustomerName = "Sara Khan" };

        await say("sara ka kuch badlna hai");
        _sent.Clear();
        await say("kuch bhi");

        Assert.Equal("Waiting", Assert.Single(await Pendings(db)).Status);
        Assert.Contains(MessageTemplates.Get("guided.customer.what", "Sara Khan"), _sent);
    }

    [Fact]
    public async Task GuidedQuestion_StoresTheOriginalCommand_AndTheTappedReference()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["ek order cancel karna hai"] = new TurnAction { Action = TurnAction.CancelOrder };

        await say("ek order cancel karna hai");
        Assert.Contains(TurnAction.CancelOrder, Assert.Single(await db.PendingActions.AsNoTracking().ToListAsync()).ActionJson);

        await say("2");
        var done = Assert.Single(await Pendings(db));
        Assert.Equal("Completed", done.Status);
        Assert.Contains("\"order\"", done.ResolvedReferencesJson);
    }
}
