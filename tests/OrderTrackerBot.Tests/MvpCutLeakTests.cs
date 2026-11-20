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

// Features:MvpOnly: the ways into a cut feature other than its typed command (a how-to, an AI intent, a follow-up state, a saved rule,
// a menu row, a button) must not run it or offer it either.
public class MvpCutLeakTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();
    private readonly List<IReadOnlyList<string>> _buttons = new();
    private readonly List<IReadOnlyList<MenuSection>> _lists = new();

    public MvpCutLeakTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, b, _) => { _sent.Add(body); _buttons.Add(b); }).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, sections, _) => { _sent.Add(body); _lists.Add(sections); }).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    public void Dispose() => _dbFactory.Dispose();

    private static string Cut => MessageTemplates.Get("mvp.001");

    private async Task<(AppDbContext Db, ConversationEngine Engine)> SetupAsync(bool mvpOnly = true, bool payments = false, ConversationState state = ConversationState.Idle)
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = state } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Shirt", Price = 1800 });
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { MvpOnly = mvpOnly, Payments = payments, ShortcutButtons = false, SuggestionButtons = false });
        return (db, engine);
    }

    [Fact]
    public async Task AddDiscount_HowTo_AnswersTheCutReply_AndTheNextMessageCreatesNoDiscount()
    {
        var (db, engine) = await SetupAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "add discount", default);
        await engine.HandleIncomingMessageAsync(Phone, "EID10, 10 percent", default);

        Assert.Equal(Cut, _sent[0]);
        Assert.Empty(await db.Discounts.ToListAsync());
        Assert.NotEqual(ConversationState.AwaitingDiscountDetails, (await db.Sessions.SingleAsync()).State);
    }

    [Fact]
    public async Task AiDiscountIntent_DoesNotOpenTheDiscountFlow()
    {
        var (db, engine) = await SetupAsync();
        using var _ = db;
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "create_discount" });

        await engine.HandleIncomingMessageAsync(Phone, "Eid ke liye customers ko discount wala offer chahiye mujhe", default);
        await engine.HandleIncomingMessageAsync(Phone, "EID10, 10 percent", default);

        Assert.Contains(Cut, _sent);
        Assert.Empty(await db.Discounts.ToListAsync());
        Assert.NotEqual(ConversationState.AwaitingDiscountDetails, (await db.Sessions.SingleAsync()).State);
    }

    [Fact]
    public async Task DiscountState_LeftFromBeforeTheCut_CreatesNoDiscount()
    {
        var (db, engine) = await SetupAsync(state: ConversationState.AwaitingDiscountDetails);
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "EID10, 10 percent", default);

        Assert.Equal(Cut, Assert.Single(_sent));
        Assert.Empty(await db.Discounts.ToListAsync());
        Assert.Equal(ConversationState.Idle, (await db.Sessions.SingleAsync()).State);
    }

    [Fact]
    public async Task PaymentMethodPrompt_IsCut_AndALeftOverStateSavesNoMethod()
    {
        var (db, engine) = await SetupAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "payment method", default);
        Assert.Equal(Cut, Assert.Single(_sent));

        var session = await db.Sessions.SingleAsync();
        session.State = ConversationState.AwaitingPaymentMethodInput;
        await db.SaveChangesAsync();
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "jazzcash, 0300-1234567", default);

        Assert.Equal(Cut, Assert.Single(_sent));
        Assert.Empty(await db.PaymentMethods.ToListAsync());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LoyaltyRuleSavedBeforeTheCut_IsNotOfferedOnANewOrder(bool mvpOnly, bool offered)
    {
        var (db, engine) = await SetupAsync(mvpOnly);
        using var _ = db;
        db.LoyaltyRules.Add(new LoyaltyRule { SellerId = (await db.Sellers.SingleAsync()).Id, OrderThreshold = 1, DiscountPercent = 10 });
        await db.SaveChangesAsync();
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order",
                IsOrderAttempt = true,
                Order = new AiOrderDraft { CustomerName = "Sara", Phone = "03001234567", Items = { new AiOrderItemDraft { ProductName = "Shirt", MatchedCatalogProductName = "Shirt", Quantity = 1 } } }
            });

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 shirt, 03001234567", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Single(await db.Orders.ToListAsync());
        Assert.Equal(offered, _sent.Any(m => m.Contains("loyalty discount")));
        Assert.Equal(offered, (await db.Sessions.SingleAsync()).State == ConversationState.AwaitingLoyaltyDiscountConfirmation);
    }

    [Fact]
    public async Task HelpList_HidesThePaymentLinkRow_WhenItIsCut()
    {
        // Payments on, so only the MVP cut can hide the row.
        var (db, engine) = await SetupAsync(payments: true);
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "help", default);

        var rows = Assert.Single(_lists).SelectMany(s => s.Rows).Select(r => r.Id).ToList();
        Assert.Contains("orders today", rows);
        Assert.DoesNotContain("payment link", rows);
    }

    [Theory]
    [InlineData("menu customers", "support queries")]
    [InlineData("menu payments", "payment link")]
    [InlineData("menu payments", "add payment")]
    public async Task MenuCategory_HidesCutRows(string menu, string cutRow)
    {
        // Payments on, so only the MVP cut can hide the payment rows.
        var (db, engine) = await SetupAsync(payments: true);
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, menu, default);

        Assert.DoesNotContain(cutRow, Assert.Single(_lists).SelectMany(s => s.Rows).Select(r => r.Id));
    }

    [Fact]
    public async Task MenuCategory_KeepsTheRows_WithoutTheMvpCut()
    {
        var (db, engine) = await SetupAsync(mvpOnly: false, payments: true);
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "menu customers", default);

        Assert.Contains("support queries", Assert.Single(_lists).SelectMany(s => s.Rows).Select(r => r.Id));
    }

    [Fact]
    public async Task BusinessSetup_DropsThePaymentMethodButton_WhenItIsCut()
    {
        var (db, engine) = await SetupAsync();
        using var _ = db;

        await engine.HandleIncomingMessageAsync(Phone, "business setup", default);

        var buttons = Assert.Single(_buttons);
        Assert.Contains("Business Info", buttons);
        Assert.DoesNotContain("Payment Method", buttons);
    }
}
