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

// A new seller's first steps with no AI reachable: found by driving a fresh seller through the webhook.
public class FirstRunTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();

    public FirstRunTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine Engine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false, SuggestionButtons = false, ProductConfirmation = true });

    private async Task<(AppDbContext Db, ConversationEngine Engine)> SetUpSellerAsync(SessionContextData? ctx = null)
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller
        {
            WhatsAppPhoneNumber = Phone, BusinessName = "Ali Store", OnboardingComplete = true, SalesMode = ctx?.AwaitingSalesMode == true ? null : "delivery",
            Session = new ConversationSession { State = ConversationState.Idle, ContextJson = (ctx ?? new SessionContextData()).ToJson() }
        };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        return (db, Engine(db));
    }

    [Fact]
    public async Task ShopNameTypedAtTheStartChoice_IsSaved_NotAskedForAgain()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        Assert.Equal(ConversationState.OnboardingStartChoice, (await db.Sessions.SingleAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "Ali Clothing", default);

        Assert.Equal("Ali Clothing", (await db.Sellers.SingleAsync()).BusinessName);
        Assert.NotEqual(ConversationState.OnboardingBusinessName, (await db.Sessions.SingleAsync()).State);
    }

    [Fact]
    public async Task TypedNumberAtTheStartChoice_PicksThatButton()
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);

        await engine.HandleIncomingMessageAsync(Phone, "1", default);

        Assert.Equal(ConversationState.OnboardingBusinessName, (await db.Sessions.SingleAsync()).State);
        Assert.Null((await db.Sellers.SingleAsync()).BusinessName);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("...")]
    public async Task NumberAtTheShopNameQuestion_IsNotSavedAsTheName(string typed)
    {
        using var db = _dbFactory.CreateContext();
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Setup shuru karein", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, typed, default);

        Assert.Null((await db.Sellers.SingleAsync()).BusinessName);
        Assert.Equal(ConversationState.OnboardingBusinessName, (await db.Sessions.SingleAsync()).State);
        Assert.Contains(MessageTemplates.Get("business.name.invalid", typed), _sent);
    }

    [Fact]
    public async Task OrderItemWrittenWithItsPackSize_MatchesTheCatalogProduct()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await engine.HandleIncomingMessageAsync(Phone, "Chini 5 kg - 950", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var chini = await db.Products.SingleAsync();
        Assert.Equal(("Chini", "kg", 5m), (chini.Name, chini.UnitType, chini.UnitQty));
        await engine.HandleIncomingMessageAsync(Phone, "customer add Bilal, Model Town, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "order for Bilal", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Chini 5 kg 2", default);

        Assert.DoesNotContain(_sent, m => m.Contains("catalog mein nahi mila"));
        Assert.Contains(_sent, m => m.Contains("Rs.1,900"));
    }

    [Fact]
    public async Task ProductWaitingForYes_DroppedByAnotherCommand_TellsTheSeller()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await engine.HandleIncomingMessageAsync(Phone, "Chawal 5 kg - 1200, stock 10", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.Contains(MessageTemplates.Get("product.pending.dropped", "Chawal"), _sent);
        Assert.Empty(await db.Products.ToListAsync());
    }

    [Fact]
    public async Task ProductWaitingForYes_ReplacedByANewProductLine_ShowsNoDroppedNotice()
    {
        var (db, engine) = await SetUpSellerAsync();
        using var _ = db;
        await engine.HandleIncomingMessageAsync(Phone, "Chawal 5 kg - 1200", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Chawal 5 kg - 1250", default);

        Assert.DoesNotContain(_sent, m => m.Contains("save nahi hua"));
    }

    [Theory]
    [InlineData("customer add Bilal 03001234567", "Bilal", "03001234567", null)]
    [InlineData("customer add Bilal Ahmed 0300 1234567", "Bilal Ahmed", "0300 1234567", null)]
    [InlineData("add customer Sara 03211234567 Model Town", "Sara", "03211234567", "Model Town")]
    public void CustomerAdd_WithoutCommas_SplitsNamePhoneAndAddress(string text, string name, string phone, string? address)
    {
        var cmd = CommandParser.TryParse(text);

        Assert.NotNull(cmd);
        Assert.Equal(CommandKind.CustomerAdd, cmd!.Kind);
        Assert.Equal((name, phone, address), (cmd.Text, cmd.Text2, cmd.Text3));
    }

    [Theory]
    [InlineData("1", "counter")]
    [InlineData("2", "delivery")]
    public async Task SalesModeQuestion_AnsweredWithATypedNumber_IsSaved(string typed, string expected)
    {
        var (db, engine) = await SetUpSellerAsync(new SessionContextData { AwaitingSalesMode = true, SalesModeJustAsked = true });
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, typed, default);

        Assert.Equal(expected, (await db.Sellers.SingleAsync()).SalesMode);
        Assert.False(SessionContextData.FromJson((await db.Sessions.SingleAsync()).ContextJson).AwaitingSalesMode);
    }
}
