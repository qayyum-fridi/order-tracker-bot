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

public class TextDistanceNearestTests
{
    [Fact]
    public void Nearest_ReturnsNearMisses_NearestFirst_AtMostTake()
    {
        var found = TextDistance.Nearest("Kurta", new[] { "Dupatta", "Kurti", "Kurtas", "Kurtaa", "Kurtab", "Shawl" }, 2, 3);
        Assert.Equal(new[] { "Kurti", "Kurtas", "Kurtaa" }, found);
    }

    [Theory]
    [InlineData("Kurti")]   // exact match is not a typo
    [InlineData("Ku")]      // too short to guess
    [InlineData("Sofa")]    // nothing close
    public void Nearest_IsEmpty_WhenExactShortOrFar(string text) =>
        Assert.Empty(TextDistance.Nearest(text, new[] { "Kurti", "Dupatta" }, 2, 3));
}

public class DidYouMeanTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string OrderText = "Sara Khan 2 Kurta 03001112222 Model Town Lahore";
    private const string LongOrderText = "Sara Khan 2 Embroidered Kurtis Set 03001112222 Model Town Lahore";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _text = new();
    private readonly List<(string Body, string[] Labels)> _buttons = new();

    public DidYouMeanTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _text.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, l, _) => _buttons.Add((b, l.ToArray()))).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), OrderText, It.IsAny<CancellationToken>())).ReturnsAsync(OrderOf("Kurta"));
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), LongOrderText, It.IsAny<CancellationToken>())).ReturnsAsync(OrderOf("Embroidered Kurtis Set"));
    }

    private static AiMessageAnalysis OrderOf(string product) => new()
    {
        Intent = "new_order",
        IsOrderAttempt = true,
        Order = new AiOrderDraft
        {
            CustomerName = "Sara Khan",
            Phone = "03001112222",
            Address = "Model Town Lahore",
            Items = { new AiOrderItemDraft { ProductName = product, Quantity = 2 } }
        }
    };

    private async Task<(ConversationEngine Engine, AppDbContext Db)> StartAsync(string[] catalog, bool buttons = true, string orderText = OrderText)
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        foreach (var (name, i) in catalog.Select((n, i) => (n, i)))
            db.Products.Add(new Product { SellerId = seller.Id, Name = name, Price = 1800 + i * 100 });
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = buttons });
        await engine.HandleIncomingMessageAsync(Phone, orderText, default);
        return (engine, db);
    }

    [Fact]
    public async Task SingleNearMiss_OffersThatProduct_AndANewProductTap()
    {
        var (_, db) = await StartAsync(new[] { "Kurti", "Dupatta" });
        using var db2 = db;

        var offer = Assert.Single(_buttons);
        Assert.Equal(new[] { "Kurti Rs.1800", "➕ Naya product" }, offer.Labels);
        Assert.Contains("aapke catalog mein nahi mila", offer.Body);
    }

    [Fact]
    public async Task ThreeNearMisses_AreAllOffered_WithPrices_AndTitlesFit20()
    {
        var (_, db) = await StartAsync(new[] { "Kurti", "Kurtas", "Kurtaa", "Shawl" });
        using var db2 = db;

        var offer = Assert.Single(_buttons);
        Assert.Equal(new[] { "Kurti Rs.1800", "Kurtas Rs.1900", "Kurtaa Rs.2000" }, offer.Labels);
        Assert.All(offer.Labels, l => Assert.True(l.Length <= 20));
    }

    [Fact]
    public async Task LongProductName_IsCutToTheButtonLimit_AndTheTapStillMapsBack()
    {
        var (engine, db) = await StartAsync(new[] { "Embroidered Kurti Set" }, orderText: LongOrderText);
        using var db2 = db;

        var offer = Assert.Single(_buttons);
        Assert.Equal("Embroidered… Rs.1800", offer.Labels[0]);
        Assert.Equal(20, offer.Labels[0].Length);

        await engine.HandleIncomingMessageAsync(Phone, offer.Labels[0], default);

        Assert.Equal(ConversationState.AwaitingOrderConfirmation, (await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync()).Session!.State);
    }

    [Fact]
    public async Task TapOnProduct_ResolvesTheDraft_LikeTypingIt()
    {
        var (engine, db) = await StartAsync(new[] { "Kurti", "Dupatta" });
        using var db2 = db;

        await engine.HandleIncomingMessageAsync(Phone, "Kurti Rs.1800", default);

        // The draft moves on (no more product question) and uses the catalog product at its catalog price.
        var session = (await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync()).Session!;
        Assert.Equal(ConversationState.AwaitingOrderConfirmation, session.State);
        Assert.Contains(_text.Concat(_buttons.Select(b => b.Body)), t => t.Contains("Kurti") && t.Contains("Total"));
        Assert.Empty(await db.Products.Where(p => p.Name == "Kurta").ToListAsync());
    }

    [Fact]
    public async Task TapOnNewProduct_AsksForThePrice()
    {
        var (engine, db) = await StartAsync(new[] { "Kurti", "Dupatta" });
        using var db2 = db;

        await engine.HandleIncomingMessageAsync(Phone, "➕ Naya product", default);

        Assert.Contains(_text, t => t.Contains("Kurta ki price kya hai"));
    }

    [Fact]
    public async Task StaleTap_AfterTheQuestionIsGone_SavesNothing_AndSaysSo()
    {
        var (engine, db) = await StartAsync(new[] { "Kurti", "Dupatta" });
        using var db2 = db;
        await engine.HandleIncomingMessageAsync(Phone, "Kurti Rs.1800", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var orders = await db.Orders.CountAsync();
        var products = await db.Products.CountAsync();

        await engine.HandleIncomingMessageAsync(Phone, "Kurti Rs.1800", default);

        Assert.Equal(orders, await db.Orders.CountAsync());
        Assert.Equal(products, await db.Products.CountAsync());
        Assert.Contains(_text, t => t.Contains("purane sawal ka tha"));
    }

    [Fact]
    public async Task NothingClose_KeepsTheTypedQuestion()
    {
        var (_, db) = await StartAsync(new[] { "Dupatta", "Shawl" });
        using var db2 = db;

        Assert.Empty(_buttons);
        Assert.Contains(_text, t => t.Contains("1️⃣") && t.Contains("2️⃣"));
    }

    [Fact]
    public async Task ButtonsOff_KeepsTheTypedHint()
    {
        var (_, db) = await StartAsync(new[] { "Kurti", "Dupatta" }, buttons: false);
        using var db2 = db;

        Assert.Empty(_buttons);
        Assert.Contains(_text, t => t.Contains("Kya aap \"Kurti\" keh rahe hain?") && t.Contains("2 Kurti"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
