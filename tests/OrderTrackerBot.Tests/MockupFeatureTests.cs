using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Screens from the UI mockup that were added in the "match mockup + tech doc" pass.</summary>
public class MockupFeatureTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();
    private readonly List<string> _sent = new();
    private readonly List<(string Body, IReadOnlyList<string> Buttons)> _buttons = new();

    public MockupFeatureTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, buttons, _) => { _buttons.Add((body, buttons)); _sent.Add(body); })
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine Engine(AppDbContext db, BillingOptions? billing = null) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, billing, _media.Object);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db, BillingOptions? billing = null, params string[] products)
    {
        var engine = Engine(db, billing);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "10 ke qareeb" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        foreach (var p in products.Length == 0 ? new[] { "Lawn Suit - 3500", "Kurti - 1800" } : products)
            await engine.HandleIncomingMessageAsync(Phone, p, default);
        await engine.HandleIncomingMessageAsync(Phone, "done", default);
        _sent.Clear();
        _buttons.Clear();
        return engine;
    }

    private void AiReturns(AiMessageAnalysis analysis) =>
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

    private static AiMessageAnalysis Order(string name, string product, int qty, string? phone = "03001234567") => new()
    {
        Intent = "new_order",
        IsOrderAttempt = true,
        Order = new AiOrderDraft { CustomerName = name, Phone = phone, Items = { new AiOrderItemDraft { ProductName = product, MatchedCatalogProductName = product, Quantity = qty } } }
    };

    [Fact]
    public async Task Onboarding_SavesOptionalDetails_AndStartsFreeTrial()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        await engine.HandleIncomingMessageAsync(Phone, "Lahore, Clothing, @ayesha.collections", default);
        Assert.Contains(_sent, m => m.Contains("Noted — Lahore, Clothing business, @ayesha.collections"));

        foreach (var m in new[] { "10", "Lawn Suit - 3500", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);

        var seller = await db.Sellers.FirstAsync();
        Assert.Equal("Lahore", seller.City);
        Assert.Equal("Clothing", seller.BusinessType);
        Assert.Equal("@ayesha.collections", seller.InstagramHandle);
        Assert.NotNull(seller.TrialEndsAt);
        Assert.Contains(_sent, m => m.Contains("14-din FREE trial"));
    }

    [Fact]
    public async Task ExpiredTrial_PausesAccess_ThenPlanAndPaidReactivate()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db, new BillingOptions { PaymentNumber = "0300-0000000" });
        var seller = await db.Sellers.FirstAsync();
        seller.TrialEndsAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);
        Assert.Contains(_sent, m => m.Contains("free trial khatam ho gaya"));
        Assert.DoesNotContain(_sent, m => m.Contains("Today's Orders") || m.Contains("Aaj koi order"));

        await engine.HandleIncomingMessageAsync(Phone, "Basic - Rs.300", default);
        Assert.Contains(_sent, m => m.Contains("Basic plan select ho gaya") && m.Contains("0300-0000000"));

        await engine.HandleIncomingMessageAsync(Phone, "paid", default);
        seller = await db.Sellers.FirstAsync();
        Assert.Equal(SubscriptionPlan.Basic, seller.Plan);
        Assert.True(seller.SubscriptionActiveUntil > DateTime.UtcNow.AddDays(27));
        _founderAlerts.Verify(f => f.NotifyAsync(seller.Id, It.Is<string>(s => s.Contains("PAID")), It.IsAny<CancellationToken>()), Times.Once);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "loyal customers", default);
        Assert.Contains(_sent, m => m.Contains("Pro plan"));
    }

    [Fact]
    public async Task ExpiredTrial_ResetAccountConfirmation_IsNotSwallowedByBilling()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db, new BillingOptions { PaymentNumber = "0300-0000000" }, "Kurti - 1800");
        var seller = await db.Sellers.FirstAsync();
        seller.TrialEndsAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        seller = await db.Sellers.FirstAsync();
        Assert.False(seller.OnboardingComplete);
        Assert.Equal(0, await db.Products.CountAsync());
        Assert.Null(seller.City);
        Assert.Null(seller.BusinessType);
        Assert.Null(seller.InstagramHandle);
        Assert.Contains(_sent, m => m.Contains("Account reset ho gaya"));
    }

    [Fact]
    public async Task Onboarding_SellerDescribingOwnStock_IsNotTreatedAsAnOrder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "10 ke qareeb" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        // Even if the model wrongly calls this an order, there is no buyer phone, so it must not end setup or start the trial.
        AiReturns(Order("Mere", "lawn suit", 4, phone: null));

        await engine.HandleIncomingMessageAsync(Phone, "mere paas 4 lawn ke suit hain, 3500 rupay", default);

        var seller = await db.Sellers.FirstAsync();
        Assert.False(seller.OnboardingComplete);
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.DoesNotContain(_sent, m => m.Contains("Yeh to order lag raha hai"));
    }

    [Fact]
    public async Task WeightProducts_QuickAdd_AndOrderShowsPackTotal()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "Sugar 5 kg - 500", default);
        Assert.Contains(_sent, m => m.Contains("Added: Sugar - 5kg - Rs.500"));
        var sugar = await db.Products.FirstAsync(p => p.Name == "Sugar");
        Assert.Equal("kg", sugar.UnitType);
        Assert.Equal(5m, sugar.UnitQty);

        AiReturns(Order("Bilal", "Sugar - 5kg", 2, "03001112222"));
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 2 sugar, 03001112222", default);
        Assert.Contains(_sent, m => m.Contains("Sugar (5kg pack) x2 = 10kg total") && m.Contains("Rs.1,000"));
    }

    [Fact]
    public async Task PriceTiers_PickTheRightRateForTheQuantity()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "Dakao Kaju - price tiers: 1kg=320, 10kg=300, 25kg=280", default);
        Assert.Contains(_sent, m => m.Contains("1-9 kg: Rs.320/kg") && m.Contains("10-24 kg: Rs.300/kg") && m.Contains("25+ kg: Rs.280/kg"));

        AiReturns(Order("Ahmed", "Dakao Kaju", 15));
        await engine.HandleIncomingMessageAsync(Phone, "Ahmed, 15kg dakao kaju, 03001234567", default);
        Assert.Contains(_sent, m => m.Contains("Rate: Rs.300/kg (10kg+ tier)") && m.Contains("Total: Rs.4,500"));

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(4500m, (await db.Orders.FirstAsync()).Total);
    }

    [Fact]
    public async Task DeliveryCharge_DefaultIsAdded_CanBeChangedWhileConfirming_AndOnASavedOrder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "delivery 200", default);
        Assert.Equal(200m, (await db.Sellers.FirstAsync()).DefaultDeliveryCharge);
        Assert.Contains(_sent, m => m.Contains("Delivery charge Rs.200 set"));

        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        Assert.Contains(_sent, m => m.Contains("Delivery: Rs.200") && m.Contains("Total: Rs.2,000"));

        // While confirming, "delivery 300" changes only this order and keeps the draft open.
        await engine.HandleIncomingMessageAsync(Phone, "delivery 300", default);
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sent, m => m.Contains("Delivery: Rs.300") && m.Contains("Total: Rs.2,100"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var order = await db.Orders.FirstAsync();
        Assert.Equal(300m, order.DeliveryCharge);
        Assert.Equal(2100m, order.Total);
        Assert.Equal(200m, (await db.Sellers.FirstAsync()).DefaultDeliveryCharge);

        await engine.HandleIncomingMessageAsync(Phone, $"order {order.Id} free delivery", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(0m, order.DeliveryCharge);
        Assert.Equal(1800m, order.Total);
    }

    [Fact]
    public async Task DeliveryCharge_WrittenInTheOrderText_IsUsed_WithoutTypingItAgain()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "delivery 200", default);
        AiReturns(Order("Sara", "Kurti", 1)); // the model didn't pick it up

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567, delivery 350", default);

        Assert.Contains(_sent, m => m.Contains("Delivery: Rs.350") && m.Contains("Total: Rs.2,150"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.FirstAsync();
        Assert.Equal(350m, order.DeliveryCharge);
        Assert.Equal(200m, (await db.Sellers.FirstAsync()).DefaultDeliveryCharge);
    }

    [Fact]
    public async Task DeliveryCharge_FromTheModel_IsUsed_AndTheSellersTextWins()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var fromModel = Order("Sara", "Kurti", 1);
        fromModel.Order!.DeliveryCharge = 150;
        AiReturns(fromModel);
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567 (screenshot jaisa)", default);
        Assert.Contains(_sent, m => m.Contains("Delivery: Rs.150"));
        await engine.HandleIncomingMessageAsync(Phone, "edit", default);

        var overridden = Order("Sara", "Kurti", 1);
        overridden.Order!.DeliveryCharge = 150;
        AiReturns(overridden);
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567, free delivery", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(0m, (await db.Orders.FirstAsync()).DeliveryCharge);
    }

    [Fact]
    public async Task DeliveryCharge_IsNotDiscounted()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        db.Discounts.Add(new OrderTrackerBot.Domain.Entities.Discount { SellerId = (await db.Sellers.FirstAsync()).Id, Code = "HALF", Type = DiscountType.Percent, Value = 50 });
        await db.SaveChangesAsync();
        await engine.HandleIncomingMessageAsync(Phone, "delivery 250", default);
        AiReturns(new AiMessageAnalysis
        {
            Intent = "new_order", IsOrderAttempt = true,
            Order = new AiOrderDraft { CustomerName = "Sara", Phone = "03001234567", DiscountCode = "HALF",
                Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } } }
        });

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567, code HALF", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var order = await db.Orders.FirstAsync();
        Assert.Equal(900m, order.DiscountAmount);   // 50% of 1800, delivery excluded
        Assert.Equal(1150m, order.Total);           // 900 + 250
    }

    [Fact]
    public async Task Returned_OnlyForShippedOrDelivered_AndDroppedFromSales()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.FirstAsync();

        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} returned", default);
        Assert.Contains(_sent, m => m.Contains("bheja hi nahi gaya") && m.Contains($"cancel order {order.Id}"));
        await db.Entry(order).ReloadAsync();
        Assert.Equal(OrderStatus.Pending, order.Status);

        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} shipped", default);
        await engine.HandleIncomingMessageAsync(Phone, "Sara ka order wapas aa gaya", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(OrderStatus.Returned, order.Status);
        Assert.NotNull(order.ReturnedAt);
        Assert.Contains(_sent, m => m.Contains("RETURNED") && m.Contains("Sales reports se"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "today's summary", default);
        Assert.Contains(_sent, m => m.Contains("Orders: 0") && m.Contains("Returned: 1") && m.Contains("Rs.0"));

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(OrderStatus.Shipped, order.Status);
    }

    private async Task<OrderTrackerBot.Domain.Entities.Order> SaveSaraKurtiOrderAsync(ConversationEngine engine, AppDbContext db, string? text = null, AiMessageAnalysis? analysis = null)
    {
        AiReturns(analysis ?? Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, text ?? "Sara, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        return await db.Orders.Include(o => o.Items).Include(o => o.Customer).OrderBy(o => o.Id).LastAsync();
    }

    [Fact]
    public async Task EditOrder_ChangesItemsCustomerAndDelivery_ThenDone_ThenUndoRestoresEverything()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var order = await SaveSaraKurtiOrderAsync(engine, db);
        Assert.Contains(_sent, m => m.Contains($"edit order {order.Id}"));

        await engine.HandleIncomingMessageAsync(Phone, $"edit order {order.Id}", default);
        Assert.Equal(ConversationState.AwaitingOrderEdit, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sent, m => m.Contains("1. Kurti x1") && m.Contains("remove 2"));

        await engine.HandleIncomingMessageAsync(Phone, "1 = 3", default);
        await engine.HandleIncomingMessageAsync(Phone, "add Lawn Suit 1", default);
        Assert.Contains(_sent, m => m.Contains("Lawn Suit x1 add") && m.Contains("Total: Rs.8,900"));
        await engine.HandleIncomingMessageAsync(Phone, "remove 1", default);
        await engine.HandleIncomingMessageAsync(Phone, "phone 0300-9998888", default);
        await engine.HandleIncomingMessageAsync(Phone, "address House 5, Gulberg", default);
        await engine.HandleIncomingMessageAsync(Phone, "delivery 200", default);
        await engine.HandleIncomingMessageAsync(Phone, "done", default);

        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sent, m => m.Contains($"Order #{order.Id} save ho gaya"));
        await db.Entry(order).ReloadAsync();
        await db.Entry(order.Customer!).ReloadAsync();
        var items = await db.OrderItems.Where(i => i.OrderId == order.Id).ToListAsync();
        Assert.Equal("Lawn Suit", Assert.Single(items).ProductNameSnapshot);
        Assert.Equal(3500m, order.Subtotal);
        Assert.Equal(200m, order.DeliveryCharge);
        Assert.Equal(3700m, order.Total);
        Assert.Equal("03009998888", order.Customer!.Phone);
        Assert.Equal("House 5, Gulberg", order.Customer.Address);

        // One undo puts the whole edit session back.
        await engine.HandleIncomingMessageAsync(Phone, "undo", default);
        await db.Entry(order).ReloadAsync();
        await db.Entry(order.Customer).ReloadAsync();
        items = await db.OrderItems.Where(i => i.OrderId == order.Id).ToListAsync();
        Assert.Equal("Kurti", Assert.Single(items).ProductNameSnapshot);
        Assert.Equal(1, items[0].Quantity);
        Assert.Equal(1800m, order.Total);
        Assert.Equal(0m, order.DeliveryCharge);
        Assert.Equal("03001234567", order.Customer.Phone);
        Assert.Null(order.Customer.Address);
    }

    [Fact]
    public async Task EditOrder_RecalculatesAPercentDiscount_AndWarnsWhenAlreadyPaid()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        db.Discounts.Add(new OrderTrackerBot.Domain.Entities.Discount { SellerId = (await db.Sellers.FirstAsync()).Id, Code = "HALF", Type = DiscountType.Percent, Value = 50 });
        await db.SaveChangesAsync();
        var order = await SaveSaraKurtiOrderAsync(engine, db, "Sara, 1 kurti, 03001234567, code HALF", new AiMessageAnalysis
        {
            Intent = "new_order", IsOrderAttempt = true,
            Order = new AiOrderDraft { CustomerName = "Sara", Phone = "03001234567", DiscountCode = "HALF",
                Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } } }
        });
        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} paid", default);

        await engine.HandleIncomingMessageAsync(Phone, $"order {order.Id} edit", default);
        await engine.HandleIncomingMessageAsync(Phone, "1 = 2", default);

        await db.Entry(order).ReloadAsync();
        Assert.Equal(3600m, order.Subtotal);
        Assert.Equal(1800m, order.DiscountAmount);
        Assert.Equal(1800m, order.Total);
        Assert.Contains(_sent, m => m.Contains("Payment PAID thi") && m.Contains("farq"));
    }

    [Fact]
    public async Task EditOrder_GuardsAndExits()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var order = await SaveSaraKurtiOrderAsync(engine, db);

        await engine.HandleIncomingMessageAsync(Phone, "edit order", default);            // latest order
        await engine.HandleIncomingMessageAsync(Phone, "remove 1", default);              // last item can't go
        Assert.Contains(_sent, m => m.Contains("aakhri item") && m.Contains($"cancel order {order.Id}"));
        await engine.HandleIncomingMessageAsync(Phone, "add Shalwar 1", default);
        Assert.Contains(_sent, m => m.Contains("\"Shalwar\" catalog mein nahi mila"));
        await engine.HandleIncomingMessageAsync(Phone, "kuch bhi", default);
        Assert.Contains(_sent, m => m.Contains("Samajh nahi aaya") && m.Contains("done"));

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);           // a real command leaves edit mode
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, $"cancel order {order.Id}", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        await engine.HandleIncomingMessageAsync(Phone, $"edit order {order.Id}", default);
        Assert.Contains(_sent, m => m.Contains("CANCELLED hai") && m.Contains("badla nahi"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task Advance_InOrderText_ThenPartPayments_UntilPaid_AndUndo()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567, advance 500 jazzcash", default);
        Assert.Contains(_sent, m => m.Contains("Advance: Rs.500 · Baqi: Rs.1,300"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.FirstAsync();
        Assert.Equal(500m, order.AmountPaid);
        Assert.Equal(PaymentStatus.Unpaid, order.PaymentStatus);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "unpaid orders", default);
        Assert.Contains(_sent, m => m.Contains("baqi Rs.1,300") && m.Contains("Total pending: Rs.1,300"));

        await engine.HandleIncomingMessageAsync(Phone, $"order {order.Id}", default);
        Assert.Contains(_sent, m => m.Contains($"📦 Order #{order.Id}") && m.Contains("PARTLY PAID — Rs.500 mila, baqi Rs.1,300") && m.Contains($"edit order {order.Id}"));

        await engine.HandleIncomingMessageAsync(Phone, $"order {order.Id} advance 800", default);
        Assert.Contains(_sent, m => m.Contains("Ab tak Rs.1,300 / Rs.1,800 — baqi Rs.500"));
        await engine.HandleIncomingMessageAsync(Phone, $"order {order.Id} paid 500", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.Equal(1800m, order.AmountPaid);

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(PaymentStatus.Unpaid, order.PaymentStatus);
        Assert.Equal(1300m, order.AmountPaid);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "today's summary", default);
        // The order is COD (the model found no payment method) with part paid up front: received money counts, not just fully-paid orders.
        Assert.Contains(_sent, m => m.Contains("Cash collected (COD): Rs.1,300"));
    }

    [Fact]
    public async Task Advance_TypedWhileConfirming_IsApplied_AndFullAdvanceMeansPaid()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);

        await engine.HandleIncomingMessageAsync(Phone, "advance 1800", default);
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sent, m => m.Contains("Advance: Rs.1,800 — poora paid"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var order = await db.Orders.FirstAsync();
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.Equal(1800m, order.AmountPaid);
    }

    [Fact]
    public async Task MarkPaid_And_CodCollected_RecordTheFullAmount()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567, advance 300", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.FirstAsync();

        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} delivered", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default); // cash collected?
        Assert.Contains(_sent, m => m.Contains("Rs.1,500 COD collected"));
        await db.Entry(order).ReloadAsync();
        Assert.Equal(1800m, order.AmountPaid);
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
    }

    private async Task<int?> StockOf(AppDbContext db, string name) =>
        (await db.Products.AsNoTracking().FirstAsync(p => p.Name == name)).StockQty;

    [Fact]
    public async Task Stock_FollowsTheOrderLifecycle_AndWarnsWhenLow()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "stock Kurti 5", default);
        Assert.Contains(_sent, m => m.Contains("Kurti — stock: 5"));

        // Save: 2 kurtis held -> 3 left, which is "low".
        AiReturns(Order("Sara", "Kurti", 2));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 2 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(3, await StockOf(db, "Kurti"));
        Assert.Contains(_sent, m => m.Contains("Stock alert") && m.Contains("Kurti: sirf 3 bache"));
        Assert.Null(await StockOf(db, "Lawn Suit")); // untracked stays untracked
        var order = await db.Orders.FirstAsync();

        // Edit 2 -> 4: two more held; undo gives them back.
        await engine.HandleIncomingMessageAsync(Phone, $"edit order {order.Id}", default);
        await engine.HandleIncomingMessageAsync(Phone, "1 = 4", default);
        await engine.HandleIncomingMessageAsync(Phone, "done", default);
        Assert.Equal(1, await StockOf(db, "Kurti"));
        await engine.HandleIncomingMessageAsync(Phone, "undo", default);
        Assert.Equal(3, await StockOf(db, "Kurti"));

        // Shipped then returned: the goods come back. Undo of the return holds them again.
        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} shipped", default);
        Assert.Equal(3, await StockOf(db, "Kurti"));
        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} returned", default);
        Assert.Equal(5, await StockOf(db, "Kurti"));
        await engine.HandleIncomingMessageAsync(Phone, "undo", default);
        Assert.Equal(3, await StockOf(db, "Kurti"));

        // Cancel releases the stock.
        await engine.HandleIncomingMessageAsync(Phone, $"cancel order {order.Id}", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(5, await StockOf(db, "Kurti"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "stock", default);
        Assert.Contains(_sent, m => m.Contains("Stock (1)") && m.Contains("Kurti: 5"));
        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);
        Assert.Contains(_sent, m => m.Contains("Kurti - Rs.1,800 (stock 5)"));
    }

    [Fact]
    public async Task Stock_AddAndOff_AndOverselling_IsFlagged()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "stock Kurti 1", default);
        await engine.HandleIncomingMessageAsync(Phone, "stock Kurti +1", default);
        Assert.Equal(2, await StockOf(db, "Kurti"));

        AiReturns(Order("Sara", "Kurti", 3));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 3 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(-1, await StockOf(db, "Kurti"));
        Assert.Contains(_sent, m => m.Contains("Kurti: stock khatam (-1)"));

        await engine.HandleIncomingMessageAsync(Phone, "stock kurti off", default);
        Assert.Null(await StockOf(db, "Kurti"));
        await engine.HandleIncomingMessageAsync(Phone, "stock Shalwar 5", default);
        Assert.Contains(_sent, m => m.Contains("\"Shalwar\" catalog mein nahi mila"));
    }

    [Fact]
    public async Task VoiceNote_IsTranscribed_Echoed_AndHandledLikeText()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        _media.Setup(m => m.DownloadAsync("voice-1", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), "audio/ogg", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync("delivery 200");
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: _media.Object, transcriber: transcriber.Object);

        await engine.HandleAudioMessageAsync(Phone, "voice-1");

        Assert.Contains(_sent, m => m == "🎤 Maine suna: \"delivery 200\"");
        Assert.Contains(_sent, m => m.Contains("Delivery charge Rs.200 set"));
        Assert.Equal(200m, (await db.Sellers.FirstAsync()).DefaultDeliveryCharge);
    }

    [Fact]
    public async Task VoiceNote_WhenTranscriptionFails_AsksToResend_AndReports()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        _media.Setup(m => m.DownloadAsync("voice-2", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        var issues = new Mock<IIssueReporter>();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: _media.Object,
            issues: issues.Object, transcriber: transcriber.Object);

        await engine.HandleAudioMessageAsync(Phone, "voice-2");

        Assert.Contains(_sent, m => m.Contains("Voice message samajh nahi aaya"));
        issues.Verify(i => i.ReportAsync(IssueCodes.VoiceTranscriptionFailed, Phone, It.IsAny<string?>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VoiceNote_WithoutTranscription_KeepsTheSendTextReply()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleAudioMessageAsync(Phone, "voice-3");

        Assert.Contains(_sent, m => m.Contains("Voice message mila") && m.Contains("TEXT"));
    }

    private async Task<ConversationEngine> VoiceEngineAtAddProductAsync(AppDbContext db, string transcript)
    {
        var setup = Engine(db);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "10 ke qareeb" })
            await setup.HandleIncomingMessageAsync(Phone, m, default);
        _sent.Clear();
        _media.Setup(m => m.DownloadAsync("voice-ctx", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), "audio/ogg", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(transcript);
        return new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: _media.Object, transcriber: transcriber.Object);
    }

    [Fact]
    public async Task VoiceNote_IsRewrittenInContext_ThenHandledByTheNormalEngine()
    {
        using var db = _dbFactory.CreateContext();
        const string spoken = "mere paas 4 lawn ke suit hain, 3500 rupay";
        var engine = await VoiceEngineAtAddProductAsync(db, spoken);
        AiVoiceContext? seen = null;
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), spoken, It.IsAny<CancellationToken>()))
            .Callback<AiVoiceContext, string, CancellationToken>((c, _, _) => seen = c)
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "Lawn Suit - 3500" } });

        await engine.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.Contains("Waiting for products to add", seen!.Situation);
        Assert.Contains(_sent, m => m.Contains($"Maine suna: \"{spoken}\"") && m.Contains("Samjha: \"Lawn Suit - 3500\""));
        Assert.True(await db.Products.AnyAsync(p => p.Name == "Lawn Suit"));
        Assert.False((await db.Sellers.FirstAsync()).OnboardingComplete);
    }

    private void AiSaysNewProducts() =>
        AiReturns(new AiMessageAnalysis
        {
            Intent = "add_products",
            NewProducts = { new AiNewProduct { Name = "Khaddar Chadar" }, new AiNewProduct { Name = "Wool Dupatta", Price = 800 } }
        });

    [Fact]
    public async Task SellerNamingProducts_IsNotAnOrder_SavesPricedOnes_AndAsksForTheRestsPrice()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiSaysNewProducts();

        await engine.HandleIncomingMessageAsync(Phone, "teen chadar khaddar ki aur do wool ke dupatte 800 ke naye products hain", default);

        Assert.True(await db.Products.AnyAsync(p => p.Name == "Wool Dupatta" && p.Price == 800));
        Assert.False(await db.Products.AnyAsync(p => p.Name == "Khaddar Chadar"));
        Assert.Contains(_sent, m => m.Contains("Wool Dupatta") && m.Contains("add ho gaye") && m.Contains("Khaddar Chadar") && m.Contains("order nahi"));
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Onboarding_NamingProductsWithoutPrices_AsksForPrices_AndStaysInSetup()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "10 ke qareeb" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        AiSaysNewProducts();

        await engine.HandleIncomingMessageAsync(Phone, "teen chadar khaddar ki aur do wool ke dupatte naye products hain", default);

        Assert.False((await db.Sellers.FirstAsync()).OnboardingComplete);
        Assert.Contains(_sent, m => m.Contains("Khaddar Chadar") && m.Contains("price"));
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task OnePriceForAllPendingProducts_PricesThem_InsteadOfCreatingAnotherProduct()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(new AiMessageAnalysis
        {
            Intent = "add_products",
            NewProducts = { new AiNewProduct { Name = "Khaddar" }, new AiNewProduct { Name = "Karandi" }, new AiNewProduct { Name = "Boski" } }
        });
        await engine.HandleIncomingMessageAsync(Phone, "teen naye products hain khaddar karandi boski", default);

        AiAnalysisContext? seen = null;
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<AiAnalysisContext, string, CancellationToken>((c, _, _) => seen = c)
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "add_products",
                NewProducts =
                {
                    new AiNewProduct { Name = "Khaddar", Price = 5000 }, new AiNewProduct { Name = "Karandi", Price = 5000 }, new AiNewProduct { Name = "Boski", Price = 5000 }
                }
            });
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Teenon ki price 5000 fi suit ke hisaab se hai", default);

        Assert.Equal(new[] { "Khaddar", "Karandi", "Boski" }, seen!.PendingPriceProducts);
        Assert.Equal(3, await db.Products.CountAsync(p => new[] { "Khaddar", "Karandi", "Boski" }.Contains(p.Name) && p.Price == 5000));
        Assert.False(await db.Products.AnyAsync(p => p.Name == "suit"));
        Assert.DoesNotContain(_sent, m => m.Contains("order nahi"));

        // Nothing is waiting any more, so the next analysis gets no pending names.
        await engine.HandleIncomingMessageAsync(Phone, "kuch aur", default);
        Assert.Empty(seen!.PendingPriceProducts);
    }

    [Fact]
    public async Task NewProduct_PriceTheSellerNeverWrote_IsDropped_AndAskedFor()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(new AiMessageAnalysis
        {
            Intent = "add_products",
            NewProducts = { new AiNewProduct { Name = "Cotton Suit", Price = 3500 } } // copied from the catalog's "Lawn Suit - 3500"
        });

        await engine.HandleIncomingMessageAsync(Phone, "teen cotton suit naye products hain", default);

        Assert.False(await db.Products.AnyAsync(p => p.Name == "Cotton Suit"));
        Assert.Contains(_sent, m => m.Contains("Cotton Suit") && m.Contains("sale price bata dein"));
    }

    [Fact]
    public async Task VoiceNote_StepsWithAnActionThatIsNotValidNow_AreDropped()
    {
        using var db = _dbFactory.CreateContext();
        const string spoken = "suit 5000";
        var engine = await VoiceEngineAtAddProductAsync(db, spoken);
        AiVoiceContext? seen = null;
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), spoken, It.IsAny<CancellationToken>()))
            .Callback<AiVoiceContext, string, CancellationToken>((c, _, _) => seen = c)
            .ReturnsAsync(new AiVoiceInterpretation
            {
                Steps = { "Suit - 5000", "setup", "yes", "done" },
                Actions = { "reply", "command", "yes", "done" }
            });

        await engine.HandleAudioMessageAsync(Phone, "voice-ctx");

        // While adding products only reply / done / skip / help are valid: the invented "setup" and "yes" never run, and "done" needs the seller to say it.
        Assert.Equal(new[] { "reply", "done", "skip", "help" }, seen!.AllowedActions);
        Assert.True(await db.Products.AnyAsync(p => p.Name == "Suit" && p.Price == 5000));
        Assert.False((await db.Sellers.AsNoTracking().FirstAsync()).OnboardingComplete);
        Assert.DoesNotContain(_sent, m => m.Contains("Kya update karna hai"));
    }

    [Fact]
    public async Task VoiceNote_WhenNoStepIsValidNow_FallsBackToTheTranscript()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var voice = VoiceEngine(db, "haan kar do");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "yes" }, Actions = { "yes" } }); // nothing is waiting for a yes
        AiReturns(new AiMessageAnalysis { Intent = "unclear" });
        _sent.Clear();

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");

        // The invented "yes" is not run: the plain transcript went through the normal engine instead (it was not understood there either).
        Assert.DoesNotContain(_sent, m => m.Contains("Samjha"));
        Assert.Contains(_sent, m => m.Contains("Maine suna: \"haan kar do\""));
    }

    [Fact]
    public async Task VoiceNote_EditSequence_KeepsEditStepsAndDone_AfterEditOrder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var order = await SaveSaraKurtiOrderAsync(engine, db);
        var voice = VoiceEngine(db, "sara ke kurti ki price pandrah sau lagao");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation
            {
                Steps = { $"edit order {order.Id}", "price 1 = 1500", "done" },
                Actions = { "command", "edit_step", "done" }
            });
        _sent.Clear();

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.Contains(_sent, m => m.Contains($"edit order {order.Id}") && m.Contains("price 1 = 1500") && m.Contains("done"));
        Assert.Equal(ConversationState.AwaitingVoiceConfirmation, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task SpokenProductWithCostAndStock_IsRemembered_UntilThePriceArrives()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(new AiMessageAnalysis
        {
            Intent = "add_products",
            NewProducts =
            {
                new AiNewProduct { Name = "Polo Shirt", Cost = 300, Stock = 10, Attributes = { ["fabric"] = "cotton" } },
                new AiNewProduct { Name = "Jeans", Cost = 9999 } // 9999 was never said: dropped
            }
        });

        await engine.HandleIncomingMessageAsync(Phone, "mere paas 10 cotton polo shirt hain jo 300 mein aati hain aur jeans", default);

        Assert.False(await db.Products.AnyAsync(p => p.Name == "Polo Shirt"));
        Assert.Contains(_sent, m => m.Contains("Polo Shirt (cost Rs.300, stock 10, fabric: cotton)") && !m.Contains("9999"));

        AiReturns(new AiMessageAnalysis
        {
            Intent = "add_products",
            NewProducts = { new AiNewProduct { Name = "Polo Shirt", Price = 500 }, new AiNewProduct { Name = "Jeans", Price = 500 } }
        });
        await engine.HandleIncomingMessageAsync(Phone, "dono ki price 500 hai", default);

        var polo = await db.Products.AsNoTracking().SingleAsync(p => p.Name == "Polo Shirt");
        Assert.Equal((500m, 300m, 10), (polo.Price, polo.CostPrice!.Value, polo.StockQty!.Value));
        Assert.Contains("cotton", polo.AttributesJson);
        Assert.Null((await db.Products.AsNoTracking().SingleAsync(p => p.Name == "Jeans")).CostPrice);
    }

    [Fact]
    public async Task ProductWithCostStockAndAttributes_SavesAllOfThem_AndEchoesWhatWasSaved()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "Polo Shirt - 500, cost 300, stock 10, color white, fabric: cotton", default);

        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Name == "Polo Shirt");
        Assert.Equal(500, product.Price);
        Assert.Equal(300, product.CostPrice);
        Assert.Equal(10, product.StockQty);
        Assert.Equal("white", product.Color);
        Assert.Contains("cotton", product.AttributesJson);
        Assert.Contains(_sent, m => m.Contains("cost Rs.300") && m.Contains("stock 10") && m.Contains("fabric: cotton"));

        // Sending it again only changes what was written.
        await engine.HandleIncomingMessageAsync(Phone, "Polo Shirt - 520, cost 310", default);
        product = await db.Products.AsNoTracking().SingleAsync(p => p.Name == "Polo Shirt");
        Assert.Equal(520, product.Price);
        Assert.Equal(310, product.CostPrice);
        Assert.Equal(10, product.StockQty);
        Assert.Equal("white", product.Color);
    }

    [Theory]
    [InlineData("Polo Shirt - 500, cost 300", true)]
    [InlineData("Polo Shirt - 500, cost price 300, qty 10", true)]
    [InlineData("Polo Shirt - 500, delivery 250", false)]
    [InlineData("Ayesha - 500, lahore", false)]
    [InlineData("Polo Shirt - 500, cost lots", false)]
    public void ProductLine_DetailsAreOnlyAcceptedWhenEveryPartIsKnown(string line, bool isProduct) =>
        Assert.Equal(isProduct, CommandParser.TryParseProductLine(line, out ProductLine? _));

    [Theory]
    [InlineData("supplier", "vendor")]
    [InlineData("Wholesaler", "vendor")]
    [InlineData("maker", "manufacturer")]
    [InlineData("Company", "manufacturer")]
    [InlineData("dept", "department")]
    [InlineData("qism", "category")]
    [InlineData(" fabric ", "fabric")]
    public void CanonicalDetailName_FoldsSynonyms_AndLeavesOtherNamesAlone(string written, string expected) =>
        Assert.Equal(expected, CommandParser.CanonicalDetailName(written));

    [Fact]
    public void ProductLine_SynonymsLandOnOneStoredName_AndCategoryHasItsOwnField()
    {
        Assert.True(CommandParser.TryParseProductLine("Polo Shirt - 500, supplier: Ali Traders, maker: Lucky, dept: Men, fabric: cotton, qism: Shirts", out ProductLine? line));
        var extras = line!.Extras!;
        Assert.Equal("Shirts", extras.Category);
        Assert.Equal("Ali Traders", extras.Attributes!["vendor"]);
        Assert.Equal("Lucky", extras.Attributes["manufacturer"]);
        Assert.Equal("Men", extras.Attributes["department"]);
        Assert.Equal("cotton", extras.Attributes["fabric"]);
        Assert.False(extras.Attributes.ContainsKey("supplier"));
    }

    [Theory]
    [InlineData("products vendor Ali Traders", "vendor", "Ali Traders")]
    [InlineData("products by category Shirts", "category", "Shirts")]
    [InlineData("catalog supplier: Ali", "vendor", "Ali")]
    [InlineData("products department Men", "department", "Men")]
    [InlineData("products vendor", "vendor", null)]
    [InlineData("products categories", "category", null)]
    public void CatalogFilter_ParsesFieldAndValue(string text, string field, string? value)
    {
        var cmd = CommandParser.TryParse(text);
        Assert.Equal(CommandKind.CatalogFilter, cmd?.Kind);
        Assert.Equal((field, value), (cmd!.Text, cmd.Text2));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("products")]
    [InlineData("Kurti - 1800")]
    public void CatalogFilter_DoesNotHijackPlainCatalogOrProductLines(string text) =>
        Assert.NotEqual(CommandKind.CatalogFilter, CommandParser.TryParse(text)?.Kind);

    [Fact]
    public async Task ProductsWithVendorAndCategory_AreSavedUnderOneName_AndCanBeFiltered()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "Polo Shirt - 500, category: Shirts, supplier: Ali Traders, department: Men", default);
        await engine.HandleIncomingMessageAsync(Phone, "Check Shirt - 700, category: Shirts, vendor: ali traders", default);
        await engine.HandleIncomingMessageAsync(Phone, "Lawn Suit - 3500, category: Suits, vendor: Noor Textiles", default);

        var polo = await db.Products.AsNoTracking().SingleAsync(p => p.Name == "Polo Shirt");
        Assert.Equal("Shirts", polo.Category);
        Assert.Contains("\"vendor\"", polo.AttributesJson);
        Assert.DoesNotContain("supplier", polo.AttributesJson);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "products vendor Ali Traders", default);
        Assert.Contains(_sent, m => m.Contains("Polo Shirt") && m.Contains("Check Shirt") && !m.Contains("Lawn Suit") && m.Contains("2 products"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "products category Suits", default);
        Assert.Contains(_sent, m => m.Contains("Lawn Suit") && !m.Contains("Polo Shirt"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "products vendor", default);
        Assert.Contains(_sent, m => m.Contains("Ali Traders (2)") && m.Contains("Noor Textiles (1)"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "products vendor Nobody", default);
        Assert.Contains(_sent, m => m.Contains("\"Nobody\" vendor wala koi product nahi mila") && m.Contains("Noor Textiles"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "products manufacturer", default);
        Assert.Contains(_sent, m => m.Contains("Manufacturer abhi kisi product par likha hua nahi"));
    }

    [Fact]
    public async Task SpokenProductCategory_GoesToTheCategoryField()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(new AiMessageAnalysis
        {
            Intent = "add_products",
            NewProducts = { new AiNewProduct { Name = "Polo Shirt", Price = 500, Attributes = { ["category"] = "Shirts", ["supplier"] = "Ali Traders" } } }
        });

        await engine.HandleIncomingMessageAsync(Phone, "mere paas polo shirt hai 500 ki, shirts category, supplier Ali Traders", default);

        var polo = await db.Products.AsNoTracking().SingleAsync(p => p.Name == "Polo Shirt");
        Assert.Equal("Shirts", polo.Category);
        Assert.Contains("\"vendor\"", polo.AttributesJson);
    }

    [Theory]
    [InlineData("suit 5000", false)]
    [InlineData("suit 5000 bas ho gaya", true)]
    public async Task VoiceNote_DoneStep_OnlyEndsSetup_WhenTheSellerSaidTheyAreFinished(string spoken, bool setupEnds)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await VoiceEngineAtAddProductAsync(db, spoken);
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), spoken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "Suit - 5000", "done" } });

        await engine.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.True(await db.Products.AnyAsync(p => p.Name == "Suit" && p.Price == 5000));
        Assert.Equal(setupEnds, (await db.Sellers.AsNoTracking().FirstAsync()).OnboardingComplete);
    }

    [Theory]
    [InlineData("catalog show")]
    [InlineData("show catalog")]
    [InlineData("catalog dikhao")]
    public void Catalog_AcceptsShowVariants(string text) =>
        Assert.Equal(CommandKind.Catalog, CommandParser.TryParse(text)?.Kind);

    private sealed class StubOpenAi(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task OpenAiAssistant_ReadsNewProducts()
    {
        var content = "{\"intent\":\"add_products\",\"is_order_attempt\":false,\"new_products\":[{\"name\":\"Cotton Suit\",\"price\":null},{\"name\":\"Wool Dupatta\",\"price\":800}]}";
        var body = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
        var assistant = new OrderTrackerBot.Infrastructure.Ai.OpenAiOrderAssistant(new HttpClient(new StubOpenAi(body)),
            Microsoft.Extensions.Options.Options.Create(new OrderTrackerBot.Infrastructure.Ai.OpenAiOptions { ApiKey = "k" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderTrackerBot.Infrastructure.Ai.OpenAiOrderAssistant>.Instance, new Mock<IIssueReporter>().Object);

        var analysis = await assistant.AnalyzeMessageAsync(new AiAnalysisContext { BusinessName = "Ayesha", Catalog = new List<AiCatalogItem>() }, "teen chadar aur do dupatte naye products hain");

        Assert.Equal("add_products", analysis.Intent);
        Assert.Collection(analysis.NewProducts,
            p => { Assert.Equal("Cotton Suit", p.Name); Assert.Null(p.Price); },
            p => { Assert.Equal("Wool Dupatta", p.Name); Assert.Equal(800m, p.Price); });
    }

    private ConversationEngine VoiceEngine(AppDbContext db, string transcript)
    {
        _media.Setup(m => m.DownloadAsync("voice-ctx", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.SetupGet(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), "audio/ogg", It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(transcript);
        return new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: _media.Object, transcriber: transcriber.Object);
    }

    [Fact]
    public async Task VoiceNote_ChangesWhatOneCustomerWasCharged_UsingRecentOrdersAsContext()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var order = await SaveSaraKurtiOrderAsync(engine, db);
        var voice = VoiceEngine(db, "sara ke kurti ki price pandrah sau lagao");
        AiVoiceContext? seen = null;
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<AiVoiceContext, string, CancellationToken>((c, _, _) => seen = c)
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { $"edit order {order.Id}", "price 1 = 1500", "done" } });

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");

        // A price change waits for YES: nothing is touched yet.
        Assert.Equal(ConversationState.AwaitingVoiceConfirmation, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sent, m => m.Contains("Yeh karoon? Reply YES ya NO."));
        Assert.Equal(1800m, (await db.OrderItems.AsNoTracking().FirstAsync(i => i.OrderId == order.Id)).UnitPrice);
        await voice.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Contains(seen!.RecentOrders, o => o.Contains($"Order #{order.Id}") && o.Contains("Sara") && o.Contains("1) Kurti x1"));
        Assert.Contains(seen.KnownCustomers, c => c.StartsWith("Sara,") && c.Contains("1 order") && c.Contains($"last Order #{order.Id}"));
        Assert.Equal(1500m, (await db.OrderItems.AsNoTracking().FirstAsync(i => i.OrderId == order.Id)).UnitPrice);
        Assert.Equal(1800m, (await db.Products.AsNoTracking().FirstAsync(p => p.Name == "Kurti")).Price); // the catalog price is only a default
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sent, m => m.Contains("Samjha:") && m.Contains("price 1 = 1500"));
    }

    [Fact]
    public async Task VoiceNote_RiskyActions_SayNo_ChangesNothing()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var order = await SaveSaraKurtiOrderAsync(engine, db);
        var voice = VoiceEngine(db, "sara ke kurti ki price pandrah sau lagao");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { $"edit order {order.Id}", "price 1 = 1500", "done" } });

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");
        await voice.HandleIncomingMessageAsync(Phone, "no", default);

        Assert.Contains(_sent, m => m.Contains("kuch nahi badla"));
        Assert.Equal(1800m, (await db.OrderItems.AsNoTracking().FirstAsync(i => i.OrderId == order.Id)).UnitPrice);
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task VoiceNote_SequenceStops_WhenEditModeDidNotOpen()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var order = await SaveSaraKurtiOrderAsync(engine, db);
        var voice = VoiceEngine(db, "order nau nau nau ki price pandrah sau lagao");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "edit order 999", "price 1 = 1500", "done" } }); // 999 = "nau nau nau"; no such order

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");
        await voice.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Contains(_sent, m => m.Contains("baqi steps nahi chalaye"));
        Assert.Equal(1800m, (await db.OrderItems.AsNoTracking().FirstAsync(i => i.OrderId == order.Id)).UnitPrice);
        Assert.NotEqual(ConversationState.AwaitingOrderEdit, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task Guidance_MidOrder_TellsWhatToDo_AndKeepsTheDraft()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sessions.FirstAsync()).State);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "kya karun", default);

        Assert.Contains(_sent, m => m.Contains("YES likhein") && m.Contains("voice note"));
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sessions.FirstAsync()).State);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Guidance_DuringOnboarding_GivesAProductExample_AndStaysOnTheStep()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "10 ke qareeb" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "?", default);

        Assert.Contains(_sent, m => m.Contains("Lawn Suit - 3500") && m.Contains("done"));
        Assert.Equal(ConversationState.OnboardingAddProduct, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task Guidance_WhenIdle_ShowsTheCommandList()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "kaise karun", default);

        Assert.Contains(_sent, m => m.Contains("Yeh commands try karein"));
    }

    [Fact]
    public async Task VoiceNote_AskingForHelp_ShowsTheTipForTheCurrentStep()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        var voice = VoiceEngine(db, "mujhe samajh nahi aa raha kya bolun");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "kya karun" } });
        _sent.Clear();

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.Contains(_sent, m => m.Contains("YES likhein"));
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task VoiceNote_SafeReads_RunWithoutConfirmation()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var voice = VoiceEngine(db, "aaj ke orders dikhao");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "orders today" } });

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.DoesNotContain(_sent, m => m.Contains("Yeh karoon?"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task VoiceNote_WithMissingDetail_AsksAQuestion_AndRunsNothing()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var order = await SaveSaraKurtiOrderAsync(engine, db);
        var voice = VoiceEngine(db, "sara ke order mein jo price lagayi woh theek nahi");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Question = "Sara ke order mein Kurti ki price kitni rakhni hai?" });
        var priceBefore = (await db.OrderItems.AsNoTracking().FirstAsync(i => i.OrderId == order.Id)).UnitPrice;

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.Contains(_sent, m => m.Contains("❓ Sara ke order mein Kurti ki price kitni rakhni hai?"));
        Assert.Equal(priceBefore, (await db.OrderItems.AsNoTracking().FirstAsync(i => i.OrderId == order.Id)).UnitPrice);
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task VoiceNote_QuestionWithShortOptions_ShowsTapButtons()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var voice = VoiceEngine(db, "hassan ka order complete kar do");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Question = "Kaunsa status lagana hai?", Options = { "mark 13 delivered", "mark 13 shipped" } });

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");

        var (body, buttons) = Assert.Single(_buttons);
        Assert.Contains("❓ Kaunsa status lagana hai?", body);
        Assert.Equal(new[] { "mark 13 delivered", "mark 13 shipped" }, buttons);
    }

    [Fact]
    public async Task VoiceNote_QuestionWithLongOptions_ShowsATapList_WithAMenuWayOut()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var voice = VoiceEngine(db, "hassan ka order complete kar do");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Question = "Kaun sa Hassan?", Options = { "mark 12 delivered (Hassan Ali)", "mark 15 delivered (Hassan Raza)" } });
        IReadOnlyList<MenuSection>? sent = null;
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, sections, _) => { _sent.Add(body); sent = sections; })
            .Returns(Task.CompletedTask);

        await voice.HandleAudioMessageAsync(Phone, "voice-ctx");

        var rows = sent!.SelectMany(s => s.Rows).ToList();
        Assert.Equal(new[] { "mark 12 delivered (Hassan Ali)", "mark 15 delivered (Hassan Raza)", "menu" }, rows.Select(r => r.Id));
        Assert.All(rows, r => Assert.True(r.Title.Length <= 24, r.Title));
        Assert.Contains(_sent, m => m.Contains("❓ Kaun sa Hassan?") && m.Contains("Hassan Raza"));
    }

    [Fact]
    public async Task VoiceNote_RewriteThatDropsANumber_AsksAgainAndRunsNothing()
    {
        using var db = _dbFactory.CreateContext();
        const string spoken = "kurti 1800";
        var engine = await VoiceEngineAtAddProductAsync(db, spoken);
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), spoken, It.IsAny<CancellationToken>())).ReturnsAsync(new AiVoiceInterpretation { Steps = { "Kurti" } });

        await engine.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.DoesNotContain(_sent, m => m.Contains("Samjha"));
        Assert.False(await db.Products.AnyAsync(p => p.Name == "Kurti"));
        Assert.Contains(_sent, m => m.Contains($"Maine suna: \"{spoken}\"") && m.Contains("alag alag"));
    }

    // The guard's three outcomes: a faithful rewrite runs; a rewrite that changes what was said, and a transcript nothing could read, both ask again.
    // An unavailable AI (no answer at all) keeps the old behaviour: the transcript goes to the deterministic engine.
    [Fact]
    public async Task VoiceNote_FaithfulRewrite_IsRun()
    {
        using var db = _dbFactory.CreateContext();
        const string spoken = "Lawn Suit paanch hazaar";
        var engine = await VoiceEngineAtAddProductAsync(db, spoken);
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), spoken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "Lawn Suit - 5000" } });

        await engine.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.Equal(5000m, (await db.Products.SingleAsync(p => p.Name == "Lawn Suit")).Price);
    }

    [Theory]
    [InlineData("Lawn Suit paanch hazaar mat likhna", "Lawn Suit - 5000")]    // a refused amount comes back
    [InlineData("Lawn Suit paanch hazaar se kam", "Lawn Suit - 5000")]        // a comparator is lost
    [InlineData("Lawn Suit paanch hazaar aur delivery", "Lawn Suit - 5000")]  // the delivery part is lost
    public async Task VoiceNote_RewriteThatChangesTheMeaning_IsNotRun_AndTheSellerIsAskedAgain(string spoken, string rewrite)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await VoiceEngineAtAddProductAsync(db, spoken);
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), spoken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { rewrite } });

        await engine.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.False(await db.Products.AnyAsync(p => p.Name == "Lawn Suit"));
        Assert.DoesNotContain(_sent, m => m.Contains("Samjha"));
        Assert.Contains(_sent, m => m.Contains($"Maine suna: \"{spoken}\"") && m.Contains("alag alag"));
    }

    [Fact]
    public async Task VoiceNote_AiAnswersWithNothingUsable_AndTheTranscriptIsNotACommand_AsksAgain()
    {
        using var db = _dbFactory.CreateContext();
        const string spoken = "bhai kuch bhi";
        var engine = await VoiceEngineAtAddProductAsync(db, spoken);
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), spoken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation());

        await engine.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.False(await db.Products.AnyAsync());
        Assert.Contains(_sent, m => m.Contains("Samajh nahi aaya"));
    }

    [Fact]
    public async Task VoiceNote_AiUnavailable_TheDeterministicEngineStillReadsTheTranscript()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await VoiceEngineAtAddProductAsync(db, "Kurti 1800");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), "Kurti 1800", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiVoiceInterpretation?)null);

        await engine.HandleAudioMessageAsync(Phone, "voice-ctx");

        Assert.Equal(1800m, (await db.Products.SingleAsync(p => p.Name == "Kurti")).Price);
    }

    [Fact]
    public async Task VoiceNote_AtOptionalDetails_SplitIntoSeveralSteps_IsAppliedAsOneAnswer()
    {
        using var db = _dbFactory.CreateContext();
        var setup = Engine(db);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections" })
            await setup.HandleIncomingMessageAsync(Phone, m, default);
        var seller = await db.Sellers.FirstAsync();
        seller.BusinessType = "Food"; // stale value from an earlier attempt
        await db.SaveChangesAsync();
        var engine = VoiceEngine(db, "سرگودھا کلوتھنگ");
        _ai.Setup(a => a.InterpretVoiceAsync(It.IsAny<AiVoiceContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiVoiceInterpretation { Steps = { "Sargodha", "Clothing" } });
        _sent.Clear();

        await engine.HandleAudioMessageAsync(Phone, "voice-ctx");

        seller = await db.Sellers.AsNoTracking().FirstAsync();
        Assert.Equal("Sargodha", seller.City);
        Assert.Equal("Clothing", seller.BusinessType);
        Assert.Equal(ConversationState.OnboardingCatalogSize, (await db.Sessions.AsNoTracking().FirstAsync()).State);
        Assert.Contains(_sent, m => m.Contains("Sargodha, Clothing business"));
    }

    [Fact]
    public async Task CustomerUpdate_ChangesPhoneAndAddress_AndUndoRestores()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await SaveSaraKurtiOrderAsync(engine, db);

        await engine.HandleIncomingMessageAsync(Phone, "Sara ka phone 0300-999 8888", default);
        Assert.Contains(_sent, m => m.Contains("Sara ka phone update: 03009998888") && m.Contains("pehle: 03001234567"));
        await engine.HandleIncomingMessageAsync(Phone, "Sara ka address House 5, Gulberg", default);
        var customer = await db.Customers.AsNoTracking().FirstAsync();
        Assert.Equal("03009998888", customer.Phone);
        Assert.Equal("House 5, Gulberg", customer.Address);

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);
        customer = await db.Customers.AsNoTracking().FirstAsync();
        Assert.Null(customer.Address);
        Assert.Equal("03009998888", customer.Phone);
    }

    [Fact]
    public async Task CustomerUpdate_AsksWhichOne_WhenTheNameIsAmbiguous()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var sellerId = (await db.Sellers.FirstAsync()).Id;
        db.Customers.AddRange(
            new OrderTrackerBot.Domain.Entities.Customer { SellerId = sellerId, Name = "Sara Khan", Phone = "03001110000" },
            new OrderTrackerBot.Domain.Entities.Customer { SellerId = sellerId, Name = "Sara Ali", Phone = "03002220000" });
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "Sara ka phone 03009998888", default);
        Assert.Contains(_sent, m => m.Contains("naam ke 2 customers") && m.Contains("Sara Khan (03001110000)") && m.Contains("Sara Ali"));

        await engine.HandleIncomingMessageAsync(Phone, "Sara Ali ka phone 03009998888", default);
        Assert.Equal("03009998888", (await db.Customers.AsNoTracking().FirstAsync(c => c.Name == "Sara Ali")).Phone);
        Assert.Equal("03001110000", (await db.Customers.AsNoTracking().FirstAsync(c => c.Name == "Sara Khan")).Phone);
    }

    [Fact]
    public async Task NewOrder_DefaultsToCod()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        Assert.Contains(_sent, m => m.Contains("Payment: COD (delivery par cash)"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(OrderPaymentMethod.Cod, (await db.Orders.FirstAsync()).PaymentMethod);
    }

    [Fact]
    public async Task MultipleCustomersInOneMessage_ConfirmAllWithOneYes()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var analysis = Order("Ayesha", "Lawn Suit", 2);
        analysis.AdditionalOrders.Add(new AiOrderDraft
        {
            CustomerName = "Bilal", Phone = "03009876543",
            Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } }
        });
        AiReturns(analysis);

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha 2 suit, Bilal 1 kurti, 03001234567 aur 03009876543", default);
        Assert.Contains(_sent, m => m.Contains("Mujhe 2 alag orders mile"));

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(2, await db.Orders.CountAsync());
        Assert.Contains(_sent, m => m.Contains("2 orders saved"));
    }

    [Fact]
    public async Task OffTopic_GetsGracefulRedirect_AndFeedbackIsLoggedAgainstOrder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        AiReturns(new AiMessageAnalysis { Intent = "off_topic", ClarificationQuestion = "Rest karein!" });
        await engine.HandleIncomingMessageAsync(Phone, "bohat thak gayi hoon aaj", default);
        Assert.Contains(_sent, m => m.Contains("Rest karein!") && m.Contains("menu"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);

        AiReturns(Order("Ayesha", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        AiReturns(new AiMessageAnalysis
        {
            Intent = "customer_feedback",
            Feedback = new AiCustomerFeedback { CustomerName = "ayesha", Text = "bahut khush thi", Sentiment = "positive" }
        });
        await engine.HandleIncomingMessageAsync(Phone, "ayesha bahut khush thi order se", default);

        var feedback = await db.CustomerFeedbacks.FirstAsync();
        Assert.NotNull(feedback.OrderId);
        Assert.Contains(_sent, m => m.Contains($"feedback saved against Order #{feedback.OrderId} (Ayesha)"));
    }

    [Fact]
    public async Task ReceiptScreenshot_MatchesUnpaidOrder_AndMarksPaid()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.FirstAsync();

        _media.Setup(m => m.DownloadAsync("media-1", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "image/jpeg"));
        _ai.Setup(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", Receipt = new AiPaymentReceipt { Amount = 1800, Provider = "JazzCash", TransactionId = "JC9928817" } });

        await engine.HandleImageMessageAsync(Phone, "media-1", null, default);
        Assert.Contains(_buttons, b => b.Body.Contains("Payment receipt mila — Rs.1,800") && b.Buttons.Contains($"Order #{order.Id} - Sara"));

        await engine.HandleIncomingMessageAsync(Phone, $"Order #{order.Id} - Sara", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.NotNull(order.PaidAt);
        Assert.Contains("JC9928817", order.Notes);
    }

    [Fact]
    public async Task ScreenshotOrder_GoesThroughTheNormalConfirmStep()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        _media.Setup(m => m.DownloadAsync("media-2", It.IsAny<CancellationToken>())).ReturnsAsync((new byte[] { 1 }, "image/jpeg"));
        var analysis = Order("Ramsha", "Lawn Suit", 2);
        _ai.Setup(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

        await engine.HandleImageMessageAsync(Phone, "media-2", null, default);

        Assert.Contains(_sent, m => m.Contains("Screenshot se order parh liya"));
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task DeleteCustomer_HidesFromList_AndRestoreBringsBack()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Bilal", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        await engine.HandleIncomingMessageAsync(Phone, "delete customer bilal", default);
        Assert.Contains(_sent, m => m.Contains("Bilal (1 orders") && m.Contains("30 din tak restore"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.NotNull((await db.Customers.FirstAsync()).DeletedAt);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "customer list", default);
        Assert.Contains(_sent, m => m.Contains("Abhi koi customer nahi hai"));

        await engine.HandleIncomingMessageAsync(Phone, "restore customer bilal", default);
        Assert.Null((await db.Customers.FirstAsync()).DeletedAt);
        Assert.Contains(_sent, m => m.Contains("Bilal wapas active ho gaya"));
    }

    [Fact]
    public async Task LoyaltyThreshold_OffersDiscount_AndYesAppliesIt()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "create loyalty: 2 orders = 10 percent off", default);
        AiReturns(Order("Ayesha", "Lawn Suit", 2));

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha, 2 suit, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        AiReturns(Order("Ayesha", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "1", default); // not a duplicate (different product), but answer defensively
        if ((await db.Sessions.FirstAsync()).State == ConversationState.AwaitingOrderConfirmation)
            await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Contains(_sent, m => m.Contains("2nd order hai") && m.Contains("10% loyalty discount"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var second = await db.Orders.OrderByDescending(o => o.Id).FirstAsync();
        Assert.Equal(1620m, second.Total);
        Assert.Contains(_sent, m => m.Contains("Loyalty discount applied") && m.Contains("Rs.180 off"));
    }

    [Fact]
    public async Task Undo_AfterNewOrder_CancelsIt()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);

        Assert.Equal(OrderStatus.Cancelled, (await db.Orders.FirstAsync()).Status);
    }

    [Fact]
    public async Task Broadcast_AsksChannelThenAudience_AndCampaignStatusReports()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiReturns(Order("Sara", "Kurti", 1));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        _sender.Setup(s => s.SendTemplateMessageAsync("923001234567", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await engine.HandleIncomingMessageAsync(Phone, "naya stock aaya — sab customers ko batao: naye lawn suits available", default);
        Assert.Contains(_buttons, b => b.Body.Contains("Konse channel"));
        await engine.HandleIncomingMessageAsync(Phone, "WhatsApp", default);
        await engine.HandleIncomingMessageAsync(Phone, "1", default);
        Assert.Contains(_sent, m => m.Contains("1 customers ko bheja gaya") && m.Contains("WhatsApp: 1 sent"));

        await engine.HandleIncomingMessageAsync(Phone, "campaign status", default);
        Assert.Contains(_sent, m => m.Contains("Last Campaign") && m.Contains("Bheja gaya: 1 customers ko"));
        Assert.Equal(1, await db.CampaignSends.CountAsync());
    }

    [Fact]
    public async Task ScheduledJobs_SendWeeklySummaryOnce_AndTrialReminder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var seller = await db.Sellers.FirstAsync();
        seller.CreatedAt = DateTime.UtcNow.AddDays(-20);
        seller.TrialEndsAt = DateTime.UtcNow.AddDays(1.5);
        await db.SaveChangesAsync();

        await engine.RunScheduledJobsAsync(DateTime.UtcNow);
        await engine.RunScheduledJobsAsync(DateTime.UtcNow);

        Assert.Single(_sent, m => m.Contains("Weekly Summary — Ayesha Collections"));
        Assert.Single(_buttons, b => b.Body.Contains("free trial") && b.Body.Contains("khatam ho raha hai"));
    }

    [Fact]
    public async Task ScheduledJobs_OneSellersFailure_DoesNotStopTheOthers_AndIsReported()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var first = await db.Sellers.FirstAsync();
        first.CreatedAt = DateTime.UtcNow.AddDays(-20);
        const string otherPhone = "923009998888";
        db.Sellers.Add(new OrderTrackerBot.Domain.Entities.Seller
        {
            WhatsAppPhoneNumber = otherPhone, BusinessName = "Second Shop", OnboardingComplete = true, CreatedAt = DateTime.UtcNow.AddDays(-20),
            Session = new OrderTrackerBot.Domain.Entities.ConversationSession { State = ConversationState.Idle }
        });
        await db.SaveChangesAsync();
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.Is<string>(t => t.Contains("Weekly Summary")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("WhatsApp down"));
        var issues = new Mock<IIssueReporter>();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, issues: issues.Object);

        await engine.RunScheduledJobsAsync(DateTime.UtcNow);

        _sender.Verify(s => s.SendTextMessageAsync(otherPhone, It.Is<string>(t => t.Contains("Weekly Summary — Second Shop")), It.IsAny<CancellationToken>()), Times.Once);
        issues.Verify(i => i.ReportAsync(IssueCodes.ScheduledJobFailed, Phone, It.IsAny<string?>(), It.IsAny<HttpRequestException>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScheduledJobs_PurgeWebhookClaimsOlderThanAWeek()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        db.ProcessedWebhookMessages.Add(new OrderTrackerBot.Domain.Entities.ProcessedWebhookMessage { MessageId = "old", ProcessedAt = DateTime.UtcNow.AddDays(-8) });
        db.ProcessedWebhookMessages.Add(new OrderTrackerBot.Domain.Entities.ProcessedWebhookMessage { MessageId = "recent", ProcessedAt = DateTime.UtcNow.AddHours(-2) });
        await db.SaveChangesAsync();

        await engine.RunScheduledJobsAsync(DateTime.UtcNow);

        Assert.Equal(new[] { "recent" }, await db.ProcessedWebhookMessages.AsNoTracking().Select(m => m.MessageId).ToArrayAsync());
    }

    [Fact]
    public async Task MessageLog_RecordsInboundAndOutbound()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);

        Assert.True(await db.MessageLogs.AnyAsync(m => m.Direction == "inbound" && m.RawText == "catalog"));
        Assert.True(await db.MessageLogs.AnyAsync(m => m.Direction == "outbound" && m.RawText.Contains("Aapka Catalog")));
    }

    [Theory]
    [InlineData("Sugar 5 kg - 500", "Sugar", "kg", 5)]
    [InlineData("Rice 10kg - 1200", "Rice", "kg", 10)]
    [InlineData("Lawn Suit - 3500", "Lawn Suit", "piece", 1)]
    public void ProductLine_SplitsUnitFromName(string line, string name, string unit, decimal qty)
    {
        Assert.True(CommandParser.TryParseProductLine(line, out ProductLine? product));
        Assert.Equal(name, product!.Name);
        Assert.Equal(unit, product.UnitType);
        Assert.Equal(qty, product.UnitQty);
    }

    [Fact]
    public void SchemaPatcher_CreatesTablesAddedAfterFirstDeploy()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE \"Products\" (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

        SqliteSchemaPatcher.Apply(db);

        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('PriceTiers','Campaigns','CampaignSends','MessageLogs')";
        Assert.Equal(4L, check.ExecuteScalar());
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Products') WHERE name IN ('UnitType','UnitQty')";
        Assert.Equal(2L, check.ExecuteScalar());
    }
}
