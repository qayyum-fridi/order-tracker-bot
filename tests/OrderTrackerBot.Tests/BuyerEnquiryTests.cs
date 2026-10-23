using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class BuyerEnquiryParserTests
{
    [Theory]
    [InlineData("enquiry Kurti blue", "Kurti blue")]
    [InlineData("buyer ne pucha Lawn Suit", "Lawn Suit")]
    [InlineData("customer ne pucha: Kurti ka rate", "Kurti ka rate")]
    public void BuyerQuestion_IsLoggedAsWritten(string text, string expected)
    {
        var parsed = CommandParser.TryParse(text);
        Assert.Equal(CommandKind.BuyerEnquiry, parsed?.Kind);
        Assert.Equal(expected, parsed?.Text);
    }

    [Theory]
    [InlineData("enquiries")]
    [InlineData("enquiry report")]
    [InlineData("enquiries last month")]
    public void EnquiryReport_IsNotALogEntry(string text) => Assert.Equal(CommandKind.EnquiryReport, CommandParser.TryParse(text)?.Kind);
}

public class BuyerEnquiryEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _text = new();
    private Seller _seller = null!;

    public BuyerEnquiryEngineTests() =>
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);

    public void Dispose() => _dbFactory.Dispose();

    private async Task<(ConversationEngine Engine, AppDbContext Db)> SetupAsync()
    {
        var db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(_seller);
        await db.SaveChangesAsync();
        return (new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(), _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false }), db);
    }

    [Fact]
    public async Task LoggedQuestion_NamesTheProductItMentions()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        db.Products.Add(new Product { SellerId = _seller.Id, Name = "Kurti", Price = 1800 });
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "enquiry Kurti blue", default);

        Assert.Contains("Note ho gaya: \"Kurti blue\"", Assert.Single(_text));
        var logged = Assert.Single(db.BuyerEnquiries);
        Assert.NotNull(logged.ProductId);
    }

    [Fact]
    public async Task Report_CountsQuestionsPerProductNextToSales()
    {
        var (engine, db) = await SetupAsync();
        using var db2 = db;
        var kurti = new Product { SellerId = _seller.Id, Name = "Kurti", Price = 1800 };
        var lawn = new Product { SellerId = _seller.Id, Name = "Lawn Suit", Price = 3500 };
        db.Products.AddRange(kurti, lawn);
        await db.SaveChangesAsync();
        var customer = new Customer { SellerId = _seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var order = new Order { SellerId = _seller.Id, CustomerId = customer.Id, Status = OrderStatus.Delivered, CreatedAt = DateTime.UtcNow.AddHours(-1), Subtotal = 1800, Total = 1800 };
        order.Items.Add(new OrderItem { ProductId = kurti.Id, ProductNameSnapshot = "Kurti", UnitPrice = 1800, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "enquiry Kurti blue", default);
        await engine.HandleIncomingMessageAsync(Phone, "buyer ne pucha Kurti", default);
        await engine.HandleIncomingMessageAsync(Phone, "enquiry Lawn Suit", default);
        _text.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "enquiries", default);

        var reply = Assert.Single(_text);
        Assert.Contains("• Kurti — pucha 2 baar, bika 1 piece", reply);
        Assert.Contains("• Lawn Suit — pucha 1 baar, bika 0 piece · ⚠️ pucha gaya, bikri nahi", reply);
        Assert.True(reply.IndexOf("Kurti —", StringComparison.Ordinal) < reply.IndexOf("Lawn Suit —", StringComparison.Ordinal));
    }
}
