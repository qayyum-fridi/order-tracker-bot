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

// Things the randomized real-shop simulation (RealShopSimulationTests) showed going wrong; each is pinned here.
public class SimulationFindingsParserTests
{
    [Theory]
    [InlineData("cancel 1", 1)]
    [InlineData("cancel #5", 5)]
    [InlineData("cancel order 3", 3)]
    public void CancelOrder_AcceptsTheShortForms(string text, int number)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.CancelOrder, parsed.Kind);
        Assert.Equal(number, parsed.Number);
    }

    [Theory]
    [InlineData("mark 1 shiped", "shipped")]
    [InlineData("mark 2 delivred", "delivered")]
    [InlineData("mark 3 pendng", "pending")]
    [InlineData("mark 4 shipped", "shipped")]
    [InlineData("mark 5 paid", "paid")]
    public void MarkStatus_ToleratesATypoInTheStatusWord(string text, string status)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.MarkStatus, parsed.Kind);
        Assert.Equal(status, parsed.Text);
    }

    [Theory]
    [InlineData("mark 3 shop")]
    [InlineData("mark 3 kurti")]
    [InlineData("mark 3 ship it now")]
    public void MarkStatus_DoesNotGuessAtOtherWords(string text) =>
        Assert.NotEqual(CommandKind.MarkStatus, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("Chadar - Rs. 2,200", "Chadar", 2200)]
    [InlineData("Kurti - Rs.1800", "Kurti", 1800)]
    [InlineData("Kurti - 1,800", "Kurti", 1800)]
    [InlineData("Kurti = PKR 1,800", "Kurti", 1800)]
    [InlineData("Kurti - 1800", "Kurti", 1800)]
    public void ProductLine_ReadsCurrencyMarkersAndThousandsCommas(string line, string name, int price)
    {
        Assert.True(CommandParser.TryParseProductLine(line, out ProductLine? product));
        Assert.Equal(name, product!.Name);
        Assert.Equal(price, product.Price);
    }

    [Theory]
    [InlineData("Price -500")]
    [InlineData("Handbag - abc")]
    [InlineData("Scarf -")]
    public void ProductLine_StillRefusesNonProducts(string line) =>
        Assert.False(CommandParser.TryParseProductLine(line, out ProductLine? _));

    [Theory]
    [InlineData("EID10 10%", "EID10, 10 percent")]
    [InlineData("EID10 10 percent", "EID10, 10 percent")]
    [InlineData("EID10, 10 percent", "EID10, 10 percent")]
    [InlineData("EID10 Rs.50 flat", "EID10, Rs.50 flat")]
    [InlineData("EID10, 10 percent, expires 15 days", "EID10, 10 percent, expires 15 days")]
    [InlineData("10%", "10 percent")]
    public void NormalizeDiscountSpec(string text, string expected) =>
        Assert.Equal(expected, CommandParser.NormalizeDiscountSpec(text));

    [Theory]
    [InlineData("Shawl - teen hazar do sau", "Shawl", 3200)]
    [InlineData("Shawl - paanch sau rupay", "Shawl", 500)]
    [InlineData("Shawl - paanch sau rupe", "Shawl", 500)]
    [InlineData("Shawl - paanch sau rupia", "Shawl", 500)]
    [InlineData("Shawl - paanch sau rp", "Shawl", 500)]
    [InlineData("Silk Suit ki price 4800 likhein", "Silk Suit", 4800)]
    [InlineData("Silk Suit ki price 4800 likhdo", "Silk Suit", 4800)]
    [InlineData("Silk Suit ki price 4800 daal do", "Silk Suit", 4800)]
    [InlineData("Silk Suit ki price 4800 krdo", "Silk Suit", 4800)]
    [InlineData("Silk Suit ki price 4800 kardo", "Silk Suit", 4800)]
    [InlineData("Shawl - dhai hazar", "Shawl", 2500)]
    [InlineData("Shawl - dedh hazar rupay", "Shawl", 1500)]
    [InlineData("Shawl - paanch sau rps", "Shawl", 500)]
    [InlineData("Khussa - 2500 dukaan mein add kar do", "Khussa", 2500)]
    [InlineData("Khussa - 2500 catalog mai add kar do", "Khussa", 2500)]
    [InlineData("Silk Suit ki price 4800 rakho", "Silk Suit", 4800)]
    [InlineData("Chadar - Rs. 2,200", "Chadar", 2200)]
    [InlineData("Dupatta 800", "Dupatta", 800)]
    [InlineData("Khussa - 2500 add kar dein", "Khussa", 2500)]
    public void OnboardingProductLine_ReadsHowSellersReallyWriteIt(string line, string name, int price)
    {
        var product = ConversationEngine.ParseLooseProductLine(line);
        Assert.NotNull(product);
        Assert.Equal(name, product!.Name);
        Assert.Equal(price, product.Price);
    }

    [Theory]
    [InlineData("Handbag - abc")]
    [InlineData("Scarf -")]
    [InlineData("Order - paanch sau ka kaam hai bhai")]
    [InlineData("Kurti - bohat acha")]
    public void OnboardingProductLine_DoesNotInventAProduct(string line) =>
        Assert.Null(ConversationEngine.ParseLooseProductLine(line));
}

public class TextDistanceTests
{
    [Theory]
    [InlineData("Ploo Shirt", "Polo Shirt", 2)]
    [InlineData("shiped", "shipped", 1)]
    [InlineData("same", "SAME", 0)]
    [InlineData("", "abc", 3)]
    public void Levenshtein(string a, string b, int expected) => Assert.Equal(expected, TextDistance.Levenshtein(a, b));

    [Fact]
    public void Closest_FindsANearMiss_NotAnExactOrFarName()
    {
        var names = new[] { "Polo Shirt", "Lawn Suit", "Kurti", "Silk Suit" };
        Assert.Equal("Polo Shirt", TextDistance.Closest("Ploo Shirt", names));
        Assert.Equal("Silk Suit", TextDistance.Closest("Slik Suit", names));
        Assert.Null(TextDistance.Closest("Polo Shirt", names));     // exact: not a typo
        Assert.Null(TextDistance.Closest("Handbag", names));        // nothing close
        Assert.Null(TextDistance.Closest("ab", names));             // too short to guess
    }
}

public class SimulationFindingsEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();

    public SimulationFindingsEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private ConversationEngine Engine(AppDbContext db) => new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>());

    private async Task<(Seller Seller, Order Order)> SeedCodOrderAsync(AppDbContext db, OrderStatus status = OrderStatus.Pending)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var customer = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Subtotal = 3500, Total = 3500, PaymentMethod = OrderPaymentMethod.Cod, Status = status };
        order.Items.Add(new OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return (seller, order);
    }

    [Fact]
    public async Task ACommandSentInsteadOfTheCodAnswer_RunsAndIsNotDroppedInSilence()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedCodOrderAsync(db, OrderStatus.Shipped);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} delivered", default);
        Assert.Equal(ConversationState.AwaitingCodCollectedConfirmation, (await db.Sessions.AsNoTracking().FirstAsync()).State);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, $"order {order.Id} advance 500", default);

        Assert.NotEmpty(_sent);
        Assert.Contains(_sent, m => m.Contains("500"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.AsNoTracking().FirstAsync()).State);
    }

    [Fact]
    public async Task YesToTheCodQuestion_StillMarksItPaid()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedCodOrderAsync(db, OrderStatus.Shipped);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} delivered", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal(PaymentStatus.Paid, (await db.Orders.AsNoTracking().FirstAsync()).PaymentStatus);
    }

    [Fact]
    public async Task NoToTheCodQuestion_LeavesItUnpaid_Quietly()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedCodOrderAsync(db, OrderStatus.Shipped);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} delivered", default);
        await engine.HandleIncomingMessageAsync(Phone, "no", default);

        Assert.Equal(PaymentStatus.Unpaid, (await db.Orders.AsNoTracking().FirstAsync()).PaymentStatus);
    }

    [Fact]
    public async Task CancelOne_AndTypoedShipped_Work()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedCodOrderAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} shiped", default);
        Assert.Equal(OrderStatus.Shipped, (await db.Orders.AsNoTracking().FirstAsync()).Status);
    }

    [Fact]
    public async Task DiscountWithAPercentSign_IsCreated()
    {
        using var db = _dbFactory.CreateContext();
        await SeedCodOrderAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "create discount: EID10 10%", default);

        var discount = await db.Discounts.AsNoTracking().SingleAsync();
        Assert.Equal("EID10", discount.Code);
        Assert.Equal(DiscountType.Percent, discount.Type);
        Assert.Equal(10m, discount.Value);
    }

    [Fact]
    public async Task AMisspelledProduct_GetsADidYouMeanHint()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, _) = await SeedCodOrderAsync(db);
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Polo Shirt", Price = 500 });
        await db.SaveChangesAsync();
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order", IsOrderAttempt = true,
                Order = new AiOrderDraft { CustomerName = "Ayesha", Phone = "0300-1111111", Items = { new AiOrderItemDraft { ProductName = "Ploo Shirt", Quantity = 2 } } }
            });

        await Engine(db).HandleIncomingMessageAsync(Phone, "Ayesha 2 Ploo Shirt, 0300-1111111", default);

        Assert.Contains(_sent, m => m.Contains("Ploo Shirt") && m.Contains("nahi mila") && m.Contains("Kya aap \"Polo Shirt\" keh rahe hain"));
    }

    [Theory]
    [InlineData("Handbag - abc", "Handbag", "abc")]
    [InlineData("Scarf -", "Scarf", "price likhi nahi")]
    public async Task UnreadablePriceDuringSetup_SaysWhichPartWasNotUnderstood(string line, string product, string detail)
    {
        using var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Shop", OnboardingComplete = false, Session = new ConversationSession { State = ConversationState.OnboardingAddProduct } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, line, default);

        Assert.Contains(_sent, m => m.Contains($"\"{product}\" ki price samajh nahi aayi") && m.Contains(detail) && m.Contains($"{product} - 2500"));
        Assert.Empty(await db.Products.ToListAsync());
    }

    public void Dispose() => _dbFactory.Dispose();
}
