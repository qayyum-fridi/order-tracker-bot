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

// The COD book by voice: what couriers still owe, a courier's payout, and a parcel's tracking number, said in own words. Writes ask YES first.
public class CodBookTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    private readonly Dictionary<string, TurnAction> _model = new();
    private int _saraOrder, _bilalOrder;

    public void Dispose() => _dbFactory.Dispose();

    private async Task<(AppDbContext Db, Func<string, Task> Say)> ShopAsync()
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", BusinessType = "Clothing", OnboardingComplete = true,
            Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var sara = new Customer { SellerId = seller.Id, Name = "Sara Khan", Phone = "03004445566" };
        var bilal = new Customer { SellerId = seller.Id, Name = "Bilal Ahmed", Phone = "03215557788" };
        db.Customers.AddRange(sara, bilal);
        await db.SaveChangesAsync();
        // Sara's COD parcel was delivered by TCS and TCS has not paid yet; Bilal's is shipped, with no tracking number.
        var delivered = new Order { SellerId = seller.Id, CustomerId = sara.Id, Status = OrderStatus.Delivered, PaymentMethod = OrderPaymentMethod.Cod,
            TrackingCourier = "TCS", Subtotal = 4000, Total = 4000, CreatedAt = DateTime.UtcNow.AddDays(-4), DeliveredAt = DateTime.UtcNow.AddDays(-2) };
        var shipped = new Order { SellerId = seller.Id, CustomerId = bilal.Id, Status = OrderStatus.Shipped, PaymentMethod = OrderPaymentMethod.Cod,
            Subtotal = 3000, Total = 3000, CreatedAt = DateTime.UtcNow.AddDays(-1) };
        foreach (var o in new[] { delivered, shipped }) o.Items.Add(new OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = o.Total, Quantity = 1 });
        db.Orders.AddRange(delivered, shipped);
        await db.SaveChangesAsync();
        (_saraOrder, _bilalOrder) = (delivered.Id, shipped.Id);

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
        var features = new FeatureOptions { ModelFirst = "on", MvpOnly = true, Payouts = true, ShortcutButtons = false, SuggestionButtons = false };

        return (db, async message =>
        {
            var engine = new ConversationEngine(db, older.Object, sender.Object, Mock.Of<IFounderAlertNotifier>(), features: features, turn: turn.Object);
            await engine.HandleIncomingMessageAsync(Phone, message, default);
        });
    }

    [Fact]
    public async Task WhatCouriersOwe_IsShown_ByCourier()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["kis kis ka paisa courier ne nahi diya"] = new TurnAction { Action = TurnAction.CodPending };

        await say("kis kis ka paisa courier ne nahi diya");

        Assert.Contains(_sent, m => m.Contains("TCS") && m.Contains($"#{_saraOrder}") && m.Contains("4,000"));
        Assert.DoesNotContain(_sent, m => m.Contains(MessageTemplates.Get("mvp.001")));
    }

    [Fact]
    public async Task ACouriersPayout_AsksYes_ThenIsRecorded()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["TCS se 4000 aa gaye"] = new TurnAction { Action = TurnAction.RecordPayout, Courier = "TCS", Amount = 4000 };

        await say("TCS se 4000 aa gaye");
        Assert.Contains(MessageTemplates.Get("model-payout.001", "TCS", "Rs.4,000"), _sent);
        Assert.Empty(await db.Payouts.ToListAsync());   // nothing before YES

        await say("yes");
        Assert.Single(await db.Payouts.ToListAsync());
    }

    [Fact]
    public async Task APayoutAmountThatWasNotSaid_IsNotRecorded()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["TCS ki payment aa gayi"] = new TurnAction { Action = TurnAction.RecordPayout, Courier = "TCS", Amount = 4000 };

        await say("TCS ki payment aa gayi");
        await say("yes");

        Assert.Empty(await db.Payouts.ToListAsync());
    }

    [Fact]
    public async Task ATrackingNumber_ForANamedCustomer_IsSavedOnTheirOrder_AfterYes()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Bilal ke parcel ka number 88231 hai Leopards"] = new TurnAction { Action = TurnAction.SetTracking, CustomerName = "Bilal", Courier = "Leopards", Value = "88231" };

        await say("Bilal ke parcel ka number 88231 hai Leopards");
        Assert.Null((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == _bilalOrder)).TrackingNumber);
        await say("yes");

        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == _bilalOrder);
        Assert.Equal("88231", order.TrackingNumber);
        Assert.Equal("Leopards", order.TrackingCourier);
        Assert.Null((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == _saraOrder)).TrackingNumber);
    }

    [Fact]
    public async Task ATrackingNumberTheSellerNeverSaid_IsNotSaved()
    {
        var (db, say) = await ShopAsync();
        using var _ = db;
        _model["Bilal ka tracking number save karo"] = new TurnAction { Action = TurnAction.SetTracking, CustomerName = "Bilal", Courier = "TCS", Value = "99999" };

        await say("Bilal ka tracking number save karo");
        await say("yes");

        Assert.Null((await db.Orders.AsNoTracking().SingleAsync(o => o.Id == _bilalOrder)).TrackingNumber);
    }
}
