using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using OrderTrackerBot.Infrastructure.WhatsApp;
using Xunit;

namespace OrderTrackerBot.Tests;

public class WalkInParserTests
{
    [Theory]
    [InlineData("walkin Kurti 2")]
    [InlineData("walk-in sale Kurti 2")]
    [InlineData("counter sale 1500")]
    [InlineData("offline order Chai - 200")]
    [InlineData("dukan par Kurti 1")]
    public void Recognised(string text) => Assert.Equal(CommandKind.WalkInSale, CommandParser.TryParse(text)?.Kind);

    [Fact]
    public void Items_Quantities_Prices_Payment_AndDay()
    {
        var a = ConversationEngine.ParseWalkIn("Kurti 2, 3 Dupatta aur Chai - 200 bank kal");
        Assert.Equal(new[] { ("Kurti", 2, (decimal?)null), ("Dupatta", 3, null), ("Chai", 1, 200m) }, a.Items.Select(i => (i.Name, i.Quantity, i.Price)));
        Assert.Equal("bank", a.Payment);
        Assert.Equal(1, a.DaysAgo);

        var b = ConversationEngine.ParseWalkIn("Rs. 1,500");
        var only = Assert.Single(b.Items);
        Assert.Equal(("Counter sale", 1, (decimal?)1500m), (only.Name, only.Quantity, only.Price));
        Assert.Null(b.Payment);
        Assert.Equal(2, ConversationEngine.ParseWalkIn("Kurti parso").DaysAgo);
    }
}

public class OfflineSalesEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();

    public OfflineSalesEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private ConversationEngine Engine(AppDbContext db) => new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>());

    private static async Task SeedAsync(AppDbContext db)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Kurti", Price = 1800 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CashWalkIn_AsksNoPhone_SavesPaidDelivered_AndCountsAsDrawerCash()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "walkin Kurti 2", default);
        Assert.Contains(_sent, m => m.Contains("Walk-in") && m.Contains("counter sale") && m.Contains("3,600"));
        Assert.DoesNotContain(_sent, m => m.Contains("phone number"));

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var order = await db.Orders.Include(o => o.Customer).SingleAsync();
        Assert.Equal("Walk-in", order.Customer!.Name);
        Assert.Null(order.Customer.Phone);
        Assert.Equal("offline", order.OrderSource);
        Assert.Equal(OrderStatus.Delivered, order.Status);
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.Equal(3600m, order.AmountPaid);
        Assert.Equal(OrderPaymentMethod.Cod, order.PaymentMethod); // cash in the drawer
        Assert.NotNull(order.PaidAt);
    }

    [Fact]
    public async Task TwoSameWalkIns_AreNotFlaggedAsDuplicates_AndShareOneCustomer()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);

        for (var i = 0; i < 2; i++)
        {
            await engine.HandleIncomingMessageAsync(Phone, "counter sale Kurti 1", default);
            await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        }

        Assert.Equal(2, await db.Orders.CountAsync());
        Assert.Equal(1, await db.Customers.CountAsync(c => c.Name == "Walk-in"));
        Assert.DoesNotContain(_sent, m => m.Contains("isi jaisa order"));
    }

    [Fact]
    public async Task AmountOnly_BankSale_Yesterday_IsBackdated_AndNotDrawerCash()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "offline sale 1500 bank kal", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var order = await db.Orders.SingleAsync();
        Assert.Equal(1500m, order.Total);
        Assert.Equal(OrderPaymentMethod.Manual, order.PaymentMethod);
        Assert.InRange(order.CreatedAt, DateTime.UtcNow.AddDays(-1).AddMinutes(-5), DateTime.UtcNow.AddDays(-1).AddMinutes(5));
    }

    [Fact]
    public async Task ADelayedMessage_KeepsItsSendTime_ForOrdersAndExpenses()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);
        var sent = DateTime.UtcNow.AddHours(-5);

        await engine.HandleIncomingMessageAsync(Phone, "expense 500 packaging", default, sent);
        await engine.HandleIncomingMessageAsync(Phone, "counter sale 900", default, sent);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default, sent.AddMinutes(1));

        Assert.InRange((await db.Expenses.SingleAsync()).CreatedAt, sent.AddSeconds(-1), sent.AddSeconds(1));
        Assert.InRange((await db.Orders.SingleAsync()).CreatedAt, sent.AddMinutes(1).AddSeconds(-1), sent.AddMinutes(1).AddSeconds(1)); // saved by the YES, sent a minute later
    }

    [Fact]
    public async Task ASendTimeInTheFutureOrTooOld_IsIgnored()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "expense 100 tea", default, DateTime.UtcNow.AddDays(2));
        await engine.HandleIncomingMessageAsync(Phone, "expense 200 tea", default, DateTime.UtcNow.AddDays(-30));

        Assert.All(await db.Expenses.ToListAsync(), e => Assert.InRange(e.CreatedAt, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void Payload_ExposesTheSendTime_ById()
    {
        var json = """{"entry":[{"changes":[{"value":{"messages":[{"id":"wamid.1","from":"923001234567","type":"text","timestamp":"1760000000","text":{"body":"hi"}}]}}]}]}""";
        var payload = System.Text.Json.JsonSerializer.Deserialize<WhatsAppWebhookPayload>(json)!;
        Assert.Equal(1760000000L, payload.SentAtById()["wamid.1"]);
    }

    public void Dispose() => _dbFactory.Dispose();
}
