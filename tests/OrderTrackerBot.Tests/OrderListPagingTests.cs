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

/// <summary>Order lists page like the catalog: 10 a page, "orders next / prev / last / page N / size N", and the same paging for order search.</summary>
public class OrderListPagingTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();
    private readonly List<(string Body, IReadOnlyList<string> Buttons)> _buttons = new();

    public OrderListPagingTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, buttons, _) => _buttons.Add((body, buttons)))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
        _buttons.Clear();
        return engine;
    }

    /// <summary>Twelve pending orders for Sara, the newest first in the list.</summary>
    private async Task SeedTwelveAsync(AppDbContext db)
    {
        var seller = await db.Sellers.FirstAsync();
        var customer = db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" }).Entity;
        await db.SaveChangesAsync();
        for (var i = 0; i < 12; i++)
        {
            var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Status = OrderStatus.Pending, Subtotal = 1800, Total = 1800, CreatedAt = DateTime.UtcNow.AddMinutes(-i - 1) };
            order.Items.Add(new OrderItem { ProductNameSnapshot = "Kurti", UnitPrice = 1800, Quantity = 1 });
            db.Orders.Add(order);
        }
        await db.SaveChangesAsync();
    }

    private string LastPageText() => _buttons.Count > 0 ? _buttons[^1].Body : _sent[^1];

    [Fact]
    public async Task PendingOrdersPageTen_AndNextShowsTheRest_WithTheirPositionsInTheWholeList()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await SeedTwelveAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "pending orders", default);
        var first = LastPageText();
        Assert.Contains("Dikh rahe hain 1–10 / 12 · Page 1/2", first);
        Assert.Contains("\n10 Sara", first);
        Assert.DoesNotContain("\n11 Sara", first);
        Assert.Contains(_buttons[^1].Buttons, b => b == "➡️ orders next");

        _buttons.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "orders next", default);
        var second = LastPageText();
        Assert.Contains("Dikh rahe hain 11–12 / 12 · Page 2/2", second);
        Assert.Contains("\n11 Sara", second);
        Assert.Contains("\n12 Sara", second);
    }

    [Fact]
    public async Task OrdersSize_ChangesThePageSizeForOrderLists()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await SeedTwelveAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);
        await engine.HandleIncomingMessageAsync(Phone, "orders size 5", default);

        Assert.Contains(_sent, m => m.Contains("Ab har page par 5 orders"));
        Assert.Contains("Dikh rahe hain 1–5 / 12 · Page 1/3", LastPageText());
    }

    [Fact]
    public async Task OrdersLast_ShowsTheFinalPage_AndPrevGoesBack()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await SeedTwelveAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "pending orders", default);
        await engine.HandleIncomingMessageAsync(Phone, "orders last", default);
        Assert.Contains("Dikh rahe hain 11–12 / 12 · Page 2/2", LastPageText());

        await engine.HandleIncomingMessageAsync(Phone, "orders prev", default);
        Assert.Contains("Dikh rahe hain 1–10 / 12 · Page 1/2", LastPageText());
    }

    [Fact]
    public async Task OrdersNextWithoutAListShownFirst_AsksForOne()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "orders next", default);

        Assert.Contains(_sent, m => m.Contains("Pehle koi order list dekhein"));
    }

    [Fact]
    public async Task OrderSearch_PagesLikeTheCatalog()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await SeedTwelveAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "search order sara", default);
        Assert.Contains("ke 12 order mile", LastPageText());
        Assert.Contains("Dikh rahe hain 1–10 / 12", LastPageText());

        await engine.HandleIncomingMessageAsync(Phone, "orders next", default);
        Assert.Contains("Dikh rahe hain 11–12 / 12", LastPageText());
    }

    public void Dispose() => _dbFactory.Dispose();
}
