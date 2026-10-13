using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ProductChoiceTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public ProductChoiceTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
        return engine;
    }

    private void OrderForUnknownProduct() =>
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order", IsOrderAttempt = true,
                Order = new AiOrderDraft { CustomerName = "Yusrah", Phone = "03457278990", Items = { new AiOrderItemDraft { ProductName = "Lawn", Quantity = 2 } } }
            });

    [Fact]
    public async Task ChoiceTwoWithName_MapsToTheExistingProduct()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        OrderForUnknownProduct();
        await engine.HandleIncomingMessageAsync(Phone, "Yusrah 03457278990 2 lawn order place karo", default);
        Assert.Contains(_sent, m => m.Contains("aapke catalog mein nahi mila"));
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "2 Kurti", default);

        Assert.DoesNotContain(_sent, m => m.Contains("Reply 1 ya 2"));
        Assert.DoesNotContain(_sent, m => m.Contains("Kaunsa existing product"));
    }

    [Theory]
    [InlineData("2")]       // asked "which existing product?" -> now waiting on a name
    [InlineData("1")]       // asked for the new product's price
    public async Task AProductLineSentWhileTheDraftWaitsOnAProduct_DropsTheDraftAndAddsTheProduct(string firstAnswer)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        OrderForUnknownProduct();
        await engine.HandleIncomingMessageAsync(Phone, "Yusrah 03457278990 2 lawn order place karo", default);
        await engine.HandleIncomingMessageAsync(Phone, firstAnswer, default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Dupatta - 600, cost 300,", default);

        Assert.DoesNotContain(_sent, m => m.Contains("catalog mein nahi mila"));
        Assert.Contains(_sent, m => m.Contains("Dupatta") && m.Contains("add ho gaya"));
        var dupatta = await db.Products.SingleAsync(p => p.Name == "Dupatta");
        Assert.Equal(600m, dupatta.Price);
        Assert.Equal(300m, dupatta.CostPrice);
        Assert.Equal(0, await db.Orders.CountAsync());                    // the half-made order is not saved
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "Shawl - 900", default);   // and the seller is no longer stuck
        Assert.Contains(_sent, m => m.Contains("Shawl") && m.Contains("add ho gaya"));
    }

    [Fact]
    public async Task ARealProductNameOrPrice_StillAnswersTheQuestion()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        OrderForUnknownProduct();
        await engine.HandleIncomingMessageAsync(Phone, "Yusrah 03457278990 2 lawn order place karo", default);
        await engine.HandleIncomingMessageAsync(Phone, "1", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Rs. 4,200", default);

        Assert.Contains(_sent, m => m.Contains("catalog mein add ho gaya"));
        Assert.Contains(_sent, m => m.Contains("Confirm order"));
    }

    [Theory]
    [InlineData("naya product: Dupatta - 600, cost 300", 600, 300, null)]
    [InlineData("Dupatta - 600, cost 300, stock 50", 600, 300, 50)]
    [InlineData("Dupatta - 600, cost 300,", 600, 300, null)]
    public async Task TheCatalogMenuWordings_AddTheProduct_ThroughTheEngine(string text, int price, int cost, int? stock)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "add product", default);   // the menu's "Add product" row
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, text, default);

        Assert.DoesNotContain(_sent, m => m.Contains("catalog mein nahi mila"));
        var dupatta = await db.Products.SingleAsync(p => p.Name == "Dupatta");
        Assert.Equal(price, dupatta.Price);
        Assert.Equal(cost, dupatta.CostPrice);
        Assert.Equal(stock, dupatta.StockQty);
    }

    [Fact]
    public async Task AddingAProductThatAlreadyExists_SaysSo_InsteadOfAnnouncingAPriceUpdate()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);               // onboarding saved "Kurti - 1800"

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1800", default);
        Assert.Contains(_sent, m => m.Contains("pehle se catalog mein hai") && m.Contains("koi tabdeeli nahi"));
        Assert.DoesNotContain(_sent, m => m.Contains("price updated"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1900", default);
        Assert.Contains(_sent, m => m.Contains("pehle se catalog mein hai") && m.Contains("Rs.1,800 → Rs.1,900"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1900, cost 1200", default);
        Assert.Contains(_sent, m => m.Contains("pehle se catalog mein hai") && m.Contains("details update") && m.Contains("1,200"));
        Assert.Equal(1, await db.Products.CountAsync(p => p.Name == "Kurti"));
    }

    [Fact]
    public async Task AddingAProductThatWasSwitchedOff_SaysItIsBack()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        (await db.Products.SingleAsync(p => p.Name == "Kurti")).IsActive = false;
        await db.SaveChangesAsync();

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1800", default);

        Assert.Contains(_sent, m => m.Contains("pehle catalog mein tha") && m.Contains("wapas chalu"));
    }

    [Fact]
    public async Task ChhoroCancelsTheDraft()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        OrderForUnknownProduct();
        await engine.HandleIncomingMessageAsync(Phone, "Yusrah 03457278990 2 lawn order place karo", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "chhoro", default);

        Assert.Contains(_sent, m => m.Contains("order cancel kar diya"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
