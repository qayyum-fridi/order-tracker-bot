using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class SupplierCourierFeeParserTests
{
    [Theory]
    [InlineData("supplier Lawn Suit Shah Alam", "set")]
    [InlineData("supplier Lawn Suit: Shah Alam", "set")]
    [InlineData("supplier Shah Alam: Lawn Suit, Kurti", "set")]
    [InlineData("Lawn Suit ka supplier Shah Alam", "of")]
    [InlineData("supplier remove Lawn Suit", "remove")]
    [InlineData("supplier hatao Lawn Suit", "remove")]
    public void SupplierCommands(string text, string action)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.SupplierSet, parsed.Kind);
        Assert.Equal(action, parsed.Text2);
    }

    [Theory]
    [InlineData("suppliers")]
    [InlineData("supplier list")]
    [InlineData("supplier report")]
    public void SupplierReport(string text)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.Insight, parsed.Kind);
        Assert.Equal("suppliers", parsed.Text);
    }

    [Theory]
    [InlineData("kis supplier ka maal sab se zyada bika")]
    [InlineData("supplier ka maal zyada bika")]
    [InlineData("Lawn Suit ka supplier kaun hai")]
    [InlineData("Lawn Suit ka supplier kaun hai?")]
    [InlineData("supplier kaun sa sab se acha hai")]
    [InlineData("supplier ko 20000 dene hain")]
    [InlineData("supplier takes 10 days to deliver")]
    [InlineData("supplier mein maal aane mein 5 din lagte hain")]
    public void QuestionsAboutSuppliers_AreNotCommands(string text) => Assert.NotEqual(CommandKind.SupplierSet, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("courier fee 12 250", 12, 250)]
    [InlineData("courier bill order 12 250", 12, 250)]
    [InlineData("order 12 courier fee 250", 12, 250)]
    [InlineData("12 courier charges 250", 12, 250)]
    [InlineData("courier fee #12 Rs.250", 12, 250)]
    [InlineData("courier kharcha 7 180.5", 7, 180.5)]
    public void CourierFee(string text, int order, double amount)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.OrderCourierFee, parsed.Kind);
        Assert.Equal(order, parsed.Number);
        Assert.Equal((decimal)amount, parsed.Amount);
    }

    [Theory]
    [InlineData("courier fee report", "courierfees")]
    [InlineData("courier ko kitna diya", "courierfees")]
    [InlineData("courier ko is mahine kitna diya", "courierfees")]
    public void CourierFeeReport(string text, string topic)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.Insight, parsed.Kind);
        Assert.Equal(topic, parsed.Text);
    }

    [Theory]
    [InlineData("courier slip")]
    [InlineData("label 12")]
    [InlineData("tracking 12 TCS 12345")]
    [InlineData("payout 11700 TCS orders 1 2 3 fee 300")]
    public void OtherCourierCommands_KeepTheirMeaning(string text) => Assert.NotEqual(CommandKind.OrderCourierFee, CommandParser.TryParse(text)?.Kind);

    [Fact]
    public void BothAreRiskyForVoice()
    {
        Assert.Contains(CommandKind.OrderCourierFee, ActionRisk.RiskyCommands);
        Assert.Contains(CommandKind.SupplierSet, ActionRisk.RiskyCommands);
    }
}

public class SupplierCourierFeeEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();
    private ConversationEngine _engine = null!;
    private AppDbContext _db = null!;
    private Seller _seller = null!;

    private async Task SetupAsync()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        _db.Sellers.Add(_seller);
        await _db.SaveChangesAsync();
        _engine = new ConversationEngine(_db, Mock.Of<IAiOrderAssistant>(), _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false });
    }

    private async Task<Product> ProductAsync(string name, decimal price, string? attributes = null)
    {
        var p = new Product { SellerId = _seller.Id, Name = name, Price = price, CostPrice = price / 2, AttributesJson = attributes };
        _db.Products.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    private async Task<Order> OrderAsync(decimal total, decimal delivery = 0, string? courier = null, OrderStatus status = OrderStatus.Delivered, DateTime? at = null, DateTime? shippedAt = null)
    {
        var c = _db.Customers.FirstOrDefault() ?? _db.Customers.Add(new Customer { SellerId = _seller.Id, Name = "Sara", Phone = "03001112222" }).Entity;
        await _db.SaveChangesAsync();
        var o = new Order { SellerId = _seller.Id, CustomerId = c.Id, Status = status, CreatedAt = at ?? DateTime.UtcNow.AddDays(-3), Subtotal = total, Total = total + delivery, DeliveryCharge = delivery, TrackingCourier = courier, ShippedAt = shippedAt ?? at ?? DateTime.UtcNow.AddDays(-3) };
        o.Items.Add(new OrderItem { ProductNameSnapshot = "Item", UnitPrice = total, Quantity = 1 });
        _db.Orders.Add(o);
        await _db.SaveChangesAsync();
        return o;
    }

    private async Task<string> AskAsync(string text)
    {
        _text.Clear();
        await _engine.HandleIncomingMessageAsync(Phone, text, default);
        _db.ChangeTracker.Clear();
        return string.Join("\n---\n", _text);
    }

    public void Dispose() => _dbFactory.Dispose();

    [Fact]
    public async Task Supplier_IsSavedOnTheProduct_KeepingTheOtherAttributes()
    {
        await SetupAsync();
        var lawn = await ProductAsync("Lawn Suit", 3500, "{\"fabric\":\"cotton\"}");

        var reply = await AskAsync("supplier Lawn Suit Shah Alam");

        Assert.Contains("Supplier \"Shah Alam\" save ho gaya", reply);
        var saved = _db.Products.Single(p => p.Id == lawn.Id).AttributesJson!;
        Assert.Contains("\"supplier\":\"Shah Alam\"", saved);
        Assert.Contains("\"fabric\":\"cotton\"", saved);

        var changed = await AskAsync("Lawn Suit ka supplier Metro");
        Assert.Contains("(pehle: Shah Alam)", changed);
        Assert.Contains("\"supplier\":\"Metro\"", _db.Products.Single(p => p.Id == lawn.Id).AttributesJson!);
    }

    [Fact]
    public async Task Supplier_FirstFormSavesSeveralProductsAtOnce_AndRefusesIfOneIsUnknown()
    {
        await SetupAsync();
        await ProductAsync("Lawn Suit", 3500);
        await ProductAsync("Kurti", 1800);

        var none = await AskAsync("supplier Shah Alam: Lawn Suit, Mystery");
        Assert.Contains("\"Mystery\" catalog mein nahi mila — kuch save nahi kiya", none);
        Assert.All(_db.Products.ToList(), p => Assert.Null(p.AttributesJson));

        var reply = await AskAsync("supplier Shah Alam: Lawn Suit, Kurti");
        Assert.Contains("• Lawn Suit", reply);
        Assert.Contains("• Kurti", reply);
        Assert.All(_db.Products.ToList(), p => Assert.Contains("Shah Alam", p.AttributesJson!));
    }

    [Fact]
    public async Task Supplier_Colon_And_Suffix_Forms()
    {
        await SetupAsync();
        await ProductAsync("Lawn Suit", 3500);
        await ProductAsync("Kurti", 1800);

        await AskAsync("supplier Lawn Suit: Shah Alam");
        await AskAsync("supplier Metro Cash Kurti");

        Assert.Contains("Shah Alam", _db.Products.Single(p => p.Name == "Lawn Suit").AttributesJson!);
        Assert.Contains("Metro Cash", _db.Products.Single(p => p.Name == "Kurti").AttributesJson!);
    }

    [Fact]
    public async Task Supplier_Remove_ClearsOnlyTheSupplier()
    {
        await SetupAsync();
        await ProductAsync("Lawn Suit", 3500, "{\"supplier\":\"Shah Alam\",\"fabric\":\"cotton\"}");

        var reply = await AskAsync("supplier remove Lawn Suit");

        Assert.Contains("supplier \"Shah Alam\" hata diya", reply);
        var saved = _db.Products.Single().AttributesJson!;
        Assert.DoesNotContain("supplier", saved);
        Assert.Contains("fabric", saved);
    }

    [Fact]
    public async Task Supplier_UnknownProduct_SaysHowToWriteIt()
    {
        await SetupAsync();
        await ProductAsync("Lawn Suit", 3500);

        var reply = await AskAsync("supplier Mystery Item Shah Alam");

        Assert.Contains("alag nahi kar saka", reply);
        Assert.Contains("supplier Lawn Suit Shah Alam", reply);
    }

    [Fact]
    public async Task SupplierReport_UsesTheSavedSupplier()
    {
        await SetupAsync();
        var lawn = await ProductAsync("Lawn Suit", 3500);
        await AskAsync("supplier Lawn Suit Shah Alam");
        var c = _db.Customers.Add(new Customer { SellerId = _seller.Id, Name = "Sara" }).Entity;
        await _db.SaveChangesAsync();
        var order = new Order { SellerId = _seller.Id, CustomerId = c.Id, Status = OrderStatus.Delivered, CreatedAt = DateTime.UtcNow.AddDays(-2), Subtotal = 7000, Total = 7000 };
        order.Items.Add(new OrderItem { ProductId = lawn.Id, ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 2, UnitCost = 1750 });
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        var reply = await AskAsync("suppliers");

        Assert.Contains("Shah Alam — 2 piece bike · sale Rs.7,000 · munafa Rs.3,500", reply);
    }

    [Fact]
    public async Task CourierFee_IsSavedOnTheOrder_WithoutTouchingTheTotal()
    {
        await SetupAsync();
        var order = await OrderAsync(2000, delivery: 200, courier: "TCS");

        var reply = await AskAsync($"courier fee {order.Id} 250");

        Assert.Contains($"Order #{order.Id}", reply);
        Assert.Contains("courier fee Rs.250 note kar li", reply);
        Assert.Contains("Rs.50 aap ki jeb se gaye", reply);
        var saved = _db.Orders.Single(o => o.Id == order.Id);
        Assert.Equal(250m, saved.CourierFee);
        Assert.Equal(2200m, saved.Total);
        Assert.Equal(200m, saved.DeliveryCharge);

        var again = await AskAsync($"order {order.Id} courier fee 150");
        Assert.Contains("(pehle Rs.250)", again);
        Assert.Contains("Rs.50 bach gaye", again);
    }

    [Fact]
    public async Task CourierFee_UnknownOrder_SaysSo()
    {
        await SetupAsync();

        var reply = await AskAsync("courier fee 99 250");

        Assert.Contains("Order #99 nahi mila", reply);
    }

    [Fact]
    public async Task CourierFeeReport_ComparesFeesWithTheDeliveryChargedPerCourier()
    {
        await SetupAsync();
        var a = await OrderAsync(1000, delivery: 200, courier: "TCS");
        var b = await OrderAsync(1000, delivery: 200, courier: "TCS");
        var c = await OrderAsync(1000, delivery: 150, courier: "Leopards");
        await OrderAsync(1000, delivery: 200, courier: "TCS");          // shipped, fee never written
        await AskAsync($"courier fee {a.Id} 250");
        await AskAsync($"courier fee {b.Id} 300");
        await AskAsync($"courier fee {c.Id} 100");

        var reply = await AskAsync("courier ko is mahine kitna diya");
        var plain = await AskAsync("courier fee report");

        foreach (var text in new[] { plain })
        {
            Assert.Contains("TCS: 2 parcels · fee Rs.550 · customers se Rs.400 → Rs.150 aap ki jeb se", text);
            Assert.Contains("Leopards: 1 parcels · fee Rs.100 · customers se Rs.150 → Rs.50 bacha", text);
            Assert.Contains("Kul courier fee: Rs.650 · customers se li gayi delivery: Rs.550 → Rs.100 aap ki jeb se gaye", text);
            Assert.Contains("1 bheje gaye parcels ki fee likhi nahi", text);
        }
        Assert.NotEmpty(reply);
    }

    [Fact]
    public async Task ReturnLoss_ShowsTheCourierBillForParcelsThatCameBack()
    {
        await SetupAsync();
        var back = await OrderAsync(1000, delivery: 200, courier: "TCS", status: OrderStatus.Returned);
        await OrderAsync(500, delivery: 150, courier: "TCS", status: OrderStatus.Returned);
        await AskAsync($"courier fee {back.Id} 260");

        var reply = await AskAsync("cancel ya return orders ki delivery charges ka kitna dead loss hua");

        Assert.Contains("Courier ko asal mein diya (jo likha gaya): Rs.260 — 1 / 2 parcels ka bill likha hai", reply);
    }
}
