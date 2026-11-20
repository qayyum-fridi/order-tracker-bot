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

// A product screenshot whose name is a longer form of a catalog product, with a colour or size: the bot asks whether it is that product's variant.
public class VariantMatchTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string PageName = "3 PIECE - EMBROIDERED PREMIUM KHADDAR SUIT";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    private readonly List<string> _rowIds = new();
    private TurnAction? _picture;

    public void Dispose() => _dbFactory.Dispose();

    // ----- Matching -----

    [Fact]
    public void ALongPageName_MatchesTheShopsShorterName_NotOneGenericWord()
    {
        var matches = ConversationEngine.VariantNameMatches(PageName, new[] { "Khaddar Suit", "Lawn Suit", "Suit", "Shirt", PageName });
        Assert.Equal(new[] { "Khaddar Suit" }, matches);
    }

    [Fact]
    public void AOneWordName_MatchesOnlyAShortPageName()
    {
        Assert.Equal(new[] { "Kurti" }, ConversationEngine.VariantNameMatches("Kurti Blue", new[] { "Kurti" }));
        Assert.Empty(ConversationEngine.VariantNameMatches("Embroidered Cotton Kurti Blue", new[] { "Kurti" }));
    }

    [Fact]
    public void TheVariantIsTheColourOrSize_AndNothingWithoutThem()
    {
        Assert.Equal("Green", ConversationEngine.VariantLabel(new AiNewProduct { Name = "x", Attributes = { ["Colour"] = "Green" } }));
        Assert.Equal("Red / Blue, size M, L", ConversationEngine.VariantLabel(new AiNewProduct { Name = "x", Colors = { "Red", "Blue" }, Sizes = { "M", "L" } }));
        Assert.Null(ConversationEngine.VariantLabel(new AiNewProduct { Name = "x", Attributes = { ["fabric"] = "Khaddar" } }));
    }

    // ----- The flow -----

    private async Task<(AppDbContext Db, Func<Task> SendPicture, Func<string, Task> Type, int SuitId)> ShopAsync()
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", BusinessType = "Clothing", OnboardingComplete = true,
            Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var suit = new Product { SellerId = seller.Id, Name = "Khaddar Suit", Price = 6500, Color = "Blue" };
        db.Products.Add(suit);
        await db.SaveChangesAsync();

        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _sent.Add(b)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, sections, _) =>
            {
                _sent.Add(body);
                _rowIds.Clear();
                _rowIds.AddRange(sections.SelectMany(s => s.Rows).Select(r => r.Id));
            })
            .Returns(Task.CompletedTask);
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((byte[] Bytes, string MimeType)?)(new byte[] { 1, 2, 3, 4 }, "image/png"));
        var turn = new Mock<ITurnInterpreter>();
        turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), It.IsAny<string>(), It.IsNotNull<AiImageInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _picture);
        var features = new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, SuggestionButtons = false };

        ConversationEngine Engine() => new(db, Mock.Of<IAiOrderAssistant>(), sender.Object, Mock.Of<IFounderAlertNotifier>(), media: media.Object,
            features: features, turn: turn.Object);
        return (db,
            async () => await Engine().HandleImageMessageAsync(Phone, "img-1", null),
            async text => await Engine().HandleIncomingMessageAsync(Phone, text, default),
            suit.Id);
    }

    private static TurnAction PageProduct(string colour = "Green") => new()
    {
        Action = TurnAction.AddProduct,
        Products = { new AiNewProduct { Name = PageName, Price = 6990, Attributes = { ["colour"] = colour, ["fabric"] = "Premium Khaddar" } } },
    };

    [Fact]
    public async Task APageForAnotherColour_IsSavedAsAVariantOfTheShopsProduct_AfterTheTapAndYes()
    {
        var (db, sendPicture, type, suitId) = await ShopAsync();
        using var _ = db;
        _picture = PageProduct();

        await sendPicture();
        Assert.Contains(_sent, m => m.StartsWith(MessageTemplates.Get("variant.match.one", "Khaddar Suit", "Green").Split('"')[0]));
        Assert.Equal(new[] { $"~{suitId}", "~new" }, _rowIds.Where(id => id.StartsWith('~')));
        Assert.Single(await db.Products.ToListAsync());   // nothing saved yet

        await type($"~{suitId}");
        Assert.Single(await db.Products.ToListAsync());   // still nothing before YES
        await type("yes");

        var suits = await db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(2, suits.Count);
        Assert.All(suits, p => Assert.Equal("Khaddar Suit", p.Name));
        Assert.Equal("Blue", suits[0].Color);
        Assert.Equal("Green", suits[1].Color);
        Assert.Equal(6990, suits[1].Price);
        Assert.Equal(6500, suits[0].Price);   // the blue one is untouched
    }

    [Fact]
    public async Task NayaProduct_KeepsThePagesOwnName()
    {
        var (db, sendPicture, type, _) = await ShopAsync();
        using var _db = db;
        _picture = PageProduct();

        await sendPicture();
        await type("~new");
        await type("yes");

        Assert.Contains(await db.Products.AsNoTracking().ToListAsync(), p => p.Name.Contains("KHADDAR SUIT") && p.Price == 6990);
    }

    [Fact]
    public async Task APageWithNoColourOrSize_GoesStraightToTheUsualConfirmation()
    {
        var (db, sendPicture, _, _) = await ShopAsync();
        using var _db = db;
        _picture = new TurnAction { Action = TurnAction.AddProduct, Products = { new AiNewProduct { Name = PageName, Price = 6990 } } };

        await sendPicture();

        Assert.DoesNotContain(_rowIds, id => id.StartsWith('~'));
        Assert.Equal(ConversationState.AwaitingModelStatusConfirmation, (await db.Sessions.AsNoTracking().SingleAsync()).State);
    }
}
