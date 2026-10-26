using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>The variant rules on their own: which listing a name means, without the database or the engine.</summary>
public class ProductVariantRulesTests
{
    private static Product Listing(string name, string? color = null, string? size = null, string unit = "piece", decimal qty = 1) =>
        new() { Name = name, Color = color, Size = size, UnitType = unit, UnitQty = qty, Price = 100 };

    [Theory]
    [InlineData("Polo Shirt white", true)]
    [InlineData("polo shirt (WHITE)", true)]
    [InlineData("Polo Shirt, white", true)]
    [InlineData("Polo Shirt white M", true)]
    [InlineData("Polo Shirt", false)]
    [InlineData("Polo Shirt red", false)]
    [InlineData("Polo Shirt white large", false)]
    [InlineData("Polo Shirtwhite", false)]
    public void NamesVariant_OnlyTheNameFollowedByItsVariantWords(string text, bool expected)
    {
        var product = Listing("Polo Shirt", color: "white", size: "M");
        Assert.Equal(expected, ProductVariants.NamesVariant(product, text));
    }

    [Fact]
    public void MatchListing_ALineWithoutAVariant_IsAmbiguousWhenSeveralVariantsAndNoPlainOne()
    {
        var listings = new List<Product> { Listing("Polo Shirt", color: "white"), Listing("Polo Shirt", color: "black") };
        Assert.Equal((null, true), ProductVariants.MatchListing(listings, size: null, color: null));
    }

    [Fact]
    public void MatchListing_ALineWithoutAVariant_PrefersThePlainListing()
    {
        var plain = Listing("Polo Shirt");
        var listings = new List<Product> { Listing("Polo Shirt", color: "white"), plain };
        Assert.Same(plain, ProductVariants.MatchListing(listings, size: null, color: null).Match);
    }

    [Fact]
    public void MatchListing_ALineWithoutAVariant_UpdatesTheOnlyListing()
    {
        var white = Listing("Polo Shirt", color: "white");
        Assert.Equal((white, false), ProductVariants.MatchListing(new List<Product> { white }, size: null, color: null));
    }

    [Fact]
    public void MatchListing_ALineNamingAColour_MatchesThatVariantOnly()
    {
        var black = Listing("Polo Shirt", color: "black");
        var listings = new List<Product> { Listing("Polo Shirt", color: "white"), black };
        Assert.Equal((black, false), ProductVariants.MatchListing(listings, size: null, color: "Black"));
    }

    [Fact]
    public void MatchListing_ANewColourOfAKnownName_IsANewListing()
    {
        var listings = new List<Product> { Listing("Polo Shirt", color: "white") };
        Assert.Equal((null, false), ProductVariants.MatchListing(listings, size: null, color: "red"));
    }

    [Fact]
    public void MatchListing_SizeAndColourAreBothPartOfTheIdentity()
    {
        var listings = new List<Product> { Listing("Polo Shirt", color: "white", size: "M"), Listing("Polo Shirt", color: "white", size: "L") };
        Assert.Equal((null, false), ProductVariants.MatchListing(listings, size: "XL", color: "white"));
    }

    [Fact]
    public void VariantsOf_SharedNameWithNoVariantNamed_ListsTheVariants()
    {
        var products = new List<Product> { Listing("Polo Shirt", color: "white"), Listing("Polo Shirt", color: "black"), Listing("Kurti") };
        Assert.Equal(new[] { "white", "black" }, ProductVariants.VariantsOf(products, "Polo Shirt").Select(p => p.Color));
    }

    [Fact]
    public void VariantsOf_ExactLabelOrPlainListing_IsNotAmbiguous()
    {
        var withPlain = new List<Product> { Listing("Polo Shirt"), Listing("Polo Shirt", color: "white") };
        Assert.Empty(ProductVariants.VariantsOf(withPlain, "Polo Shirt"));

        var twoVariants = new List<Product> { Listing("Polo Shirt", color: "white"), Listing("Polo Shirt", color: "black") };
        Assert.Empty(ProductVariants.VariantsOf(twoVariants, "Polo Shirt (white)"));
    }

    [Fact]
    public void VariantsOf_PackSizesOfOneNameAreNotVariants()
    {
        var packs = new List<Product> { Listing("Sugar"), Listing("Sugar", unit: "kg", qty: 5) };
        Assert.Empty(ProductVariants.VariantsOf(packs, "Sugar"));
    }

    [Fact]
    public void SnapshotName_KeepsTheVariantInTheOrderLine()
    {
        Assert.Equal("Polo Shirt (white, M)", ProductVariants.SnapshotName(Listing("Polo Shirt", color: "white", size: "M")));
        Assert.Equal("Polo Shirt", ProductVariants.SnapshotName(Listing("Polo Shirt")));
    }
}

/// <summary>Size and colour variants through the engine: saved as separate listings, picked by name in orders, asked about when a name is shared.</summary>
public class ProductVariantTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();
    private readonly List<string> _lists = new();

    public ProductVariantTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        // The variant question is a list message: its body is captured here.
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _lists.Add(body))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
        _lists.Clear();
        return engine;
    }

    /// <summary>Polo Shirt in white (stock 10) and black (stock 4), saved the way a seller types them.</summary>
    private async Task<ConversationEngine> SeedPoloVariantsAsync(AppDbContext db)
    {
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "Polo Shirt - 500, color white, stock 10", default);
        await engine.HandleIncomingMessageAsync(Phone, "Polo Shirt - 500, color black, stock 4", default);
        _sent.Clear();
        _lists.Clear();
        return engine;
    }

    private void OrderFor(string productName, int quantity = 2) =>
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order", IsOrderAttempt = true,
                Order = new AiOrderDraft { CustomerName = "Ayesha", Phone = "03001234567", Items = { new AiOrderItemDraft { ProductName = productName, Quantity = quantity } } }
            });

    private static async Task<List<Product>> PolosAsync(AppDbContext db) =>
        await db.Products.AsNoTracking().Where(p => p.Name == "Polo Shirt").OrderBy(p => p.Id).ToListAsync();

    public void Dispose() => _dbFactory.Dispose();

    [Fact]
    public async Task TheSameNameInTwoColours_IsTwoSellableListings_EachWithItsOwnStock()
    {
        using var db = _dbFactory.CreateContext();
        await SeedPoloVariantsAsync(db);

        var polos = await PolosAsync(db);
        Assert.Equal(new[] { ("white", 10), ("black", 4) }, polos.Select(p => (p.Color ?? "", p.StockQty!.Value)));
    }

    [Fact]
    public async Task ANewColourOfAKnownName_IsAddedAndTheSiblingsAreNamed()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await SeedPoloVariantsAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "Polo Shirt - 500, color red", default);

        Assert.Equal(3, (await PolosAsync(db)).Count);
        Assert.Contains(_sent, m => m.Contains("Is naam ke doosre variants") && m.Contains("Polo Shirt (white)") && m.Contains("Polo Shirt (black)"));
    }

    [Fact]
    public async Task APriceLineWithoutAVariant_WhenTheNameHasSeveral_ChangesNothingAndAsks()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await SeedPoloVariantsAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "Polo Shirt - 520", default);

        Assert.Equal(new[] { 500m, 500m }, (await PolosAsync(db)).Select(p => p.Price));
        Assert.Contains(_sent, m => m.Contains("kai variants hain") && m.Contains("Polo Shirt (white), Polo Shirt (black)"));
    }

    [Fact]
    public async Task EditProductWithAVariantLabel_ChangesOnlyThatVariant()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await SeedPoloVariantsAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "edit product: Polo Shirt (black) - 550", default);

        Assert.Equal(new[] { 500m, 550m }, (await PolosAsync(db)).Select(p => p.Price));
    }

    [Fact]
    public async Task EditProductWithTheBareName_NamesTheVariantsInsteadOfPickingOne()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await SeedPoloVariantsAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "edit product: Polo Shirt - 550", default);

        Assert.Equal(new[] { 500m, 500m }, (await PolosAsync(db)).Select(p => p.Price));
        Assert.Contains(_sent, m => m.Contains("kai variants hain"));
    }

    [Fact]
    public async Task StockWithTheVariantLabel_CountsThatVariantOnly()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await SeedPoloVariantsAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "stock Polo Shirt (black) 7", default);

        Assert.Equal(new[] { 10, 7 }, (await PolosAsync(db)).Select(p => p.StockQty!.Value));
    }

    [Fact]
    public async Task AnOrderForTheBareName_AsksWhichVariant_ThenSavesTheChosenOneAndItsStock()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await SeedPoloVariantsAsync(db);
        OrderFor("Polo Shirt", 2);

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha ka order, 2 polo shirt", default);

        Assert.Contains(_lists, body => body.Contains("kai variants hain"));
        Assert.DoesNotContain(_sent, m => m.Contains("Confirm order"));

        await engine.HandleIncomingMessageAsync(Phone, "2", default);   // black
        Assert.Contains(_sent, m => m.Contains("Polo Shirt (black)"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var item = await db.OrderItems.AsNoTracking().SingleAsync();
        Assert.Equal("Polo Shirt (black)", item.ProductNameSnapshot);
        Assert.Equal((await PolosAsync(db)).Single(p => p.Color == "black").Id, item.ProductId);
        Assert.Equal(new[] { 10, 2 }, (await PolosAsync(db)).Select(p => p.StockQty!.Value));
    }

    [Fact]
    public async Task AnOrderNamingTheVariant_NeedsNoQuestion()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await SeedPoloVariantsAsync(db);
        OrderFor("Polo Shirt white", 1);

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha ka order, polo shirt white", default);

        Assert.Empty(_lists);
        Assert.Contains(_sent, m => m.Contains("Polo Shirt (white)"));
    }

    [Fact]
    public async Task AnAnswerOutsideTheChoices_AsksAgain()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await SeedPoloVariantsAsync(db);
        OrderFor("Polo Shirt", 1);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha ka order, polo shirt", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "9", default);

        Assert.Contains(_sent, m => m.Contains("variant ka number"));
        Assert.Empty(await db.Orders.ToListAsync());
    }
}
