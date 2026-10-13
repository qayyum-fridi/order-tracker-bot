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

public class NextActionParserTests
{
    [Theory]
    [InlineData("order for Sara", "Sara")]
    [InlineData("Sara ke liye order", "Sara")]
    [InlineData("Sara Khan ko order dena", "Sara Khan")]
    public void OrderForCustomer_IsRecognised(string text, string name)
    {
        var parsed = CommandParser.TryParse(text)!;
        Assert.Equal(CommandKind.OrderForCustomer, parsed.Kind);
        Assert.Equal(name, parsed.Text);
    }

    [Theory]
    [InlineData("order for 12")]
    [InlineData("Sara ko order kab dena hai bhai")]
    public void OtherSentences_AreNotOrderFor(string text) => Assert.NotEqual(CommandKind.OrderForCustomer, CommandParser.TryParse(text)?.Kind);
}

public class NextActionEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();
    private readonly List<string[]> _bars = new();

    public NextActionEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, labels, _) => { _sent.Add(body); _bars.Add(labels.ToArray()); }).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private ConversationEngine Engine(AppDbContext db) => new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions());

    private static async Task SeedAsync(AppDbContext db)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222", Address = "Lahore" });
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Kurti", Price = 1800 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AfterAddingAProduct_TheBarOffersWhatUsuallyFollows_NotTheGenericOnes()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "Shawl - 2500", default);

        var bar = Assert.Single(_bars);
        Assert.Equal(new[] { "walkin Shawl 1", "catalog", "stock" }, bar);
        Assert.All(bar, l => Assert.NotNull(CommandParser.TryParse(l)));
    }

    [Fact]
    public async Task ALongProductName_DropsTheButtonThatWouldNotFit_InsteadOfCuttingIt()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "Embroidered Bridal Lehenga - 25000", default);

        var bar = Assert.Single(_bars);
        Assert.DoesNotContain(bar, l => l.StartsWith("walkin"));
        Assert.All(bar, l => Assert.InRange(l.Length, 1, 20));
    }

    [Fact]
    public async Task AfterACustomerCorrection_TheBarOffersAnOrderForThemAndTheirHistory()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "Sara ka phone 03005556666", default);

        Assert.Equal(new[] { "order for Sara", "Sara ka order", "undo" }, Assert.Single(_bars));
    }

    [Fact]
    public async Task OrderForCustomer_TakesTheNextMessageAsItems_WithPhoneAndAddressAlreadyKnown()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);
        Assert.Contains(_sent, m => m.Contains("Sara ke liye order"));
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Kurti 2", default);
        Assert.Contains(_sent, m => m.Contains("Customer: Sara") && m.Contains("3,600") && m.Contains("03001112222"));
        Assert.DoesNotContain(_sent, m => m.Contains("phone number"));

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.Include(o => o.Customer).SingleAsync();
        Assert.Equal("Sara", order.Customer!.Name);
        Assert.Equal(OrderStatus.Pending, order.Status);   // a normal order, unlike a counter sale
        Assert.Equal(1, await db.Customers.CountAsync());   // no second Sara
    }

    [Fact]
    public async Task OrderForCustomer_ARealCommandInstead_DropsTheRequestAndRuns()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "order for Sara", default);
        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "Kurti 2", default); // no longer "for Sara"

        Assert.DoesNotContain(_sent, m => m.Contains("Customer: Sara"));
    }

    [Fact]
    public async Task OrderForAnUnknownCustomer_SaysHowToStartOne()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "order for Zainab", default);

        Assert.Contains(_sent, m => m.Contains("saved nahi"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
