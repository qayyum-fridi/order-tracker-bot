using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ConversationEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sentMessages = new();

    public ConversationEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sentMessages.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task OnboardSellerAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "10 ke qareeb", default);
        await engine.HandleIncomingMessageAsync(Phone, "Lawn Suit - 3500", default);
        await engine.HandleIncomingMessageAsync(Phone, "Kurti - 1800", default);
        await engine.HandleIncomingMessageAsync(Phone, "done", default);
        _sentMessages.Clear();
    }

    [Fact]
    public async Task Onboarding_CompletesAndSavesCatalog()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);

        var seller = await db.Sellers.Include(s => s.Products).FirstAsync(s => s.WhatsAppPhoneNumber == Phone);
        Assert.True(seller.OnboardingComplete);
        Assert.Equal("Ayesha Collections", seller.BusinessName);
        Assert.Equal(2, seller.Products.Count);
    }

    [Fact]
    public async Task ResetAccount_WithConfirmation_WipesDataAndRestartsOnboarding()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var seller = await db.Sellers.Include(s => s.Session).FirstAsync(s => s.WhatsAppPhoneNumber == Phone);
        Assert.False(seller.OnboardingComplete);
        Assert.Null(seller.BusinessName);
        Assert.Equal(ConversationState.OnboardingLanguage, seller.Session!.State);
        Assert.Equal(0, await db.Products.CountAsync());
        Assert.Contains(_sentMessages, m => m.Contains("Order Assistant"));
        _sender.Verify(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(l => l.Count == 3), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ResetAccount_WithoutConfirmation_KeepsData()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "no", default);

        Assert.Equal(2, await db.Products.CountAsync());
        Assert.True((await db.Sellers.FirstAsync()).OnboardingComplete);
    }

    [Fact]
    public async Task MidOnboardingCommand_IsDeferredNotExecuted()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        await engine.HandleIncomingMessageAsync(Phone, "Ayesha Collections", default);
        await engine.HandleIncomingMessageAsync(Phone, "10 ke qareeb", default);
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.Contains(_sentMessages, m => m.Contains("pehle catalog complete karein"));
        var orderCount = await db.Orders.CountAsync();
        Assert.Equal(0, orderCount);
    }

    [Fact]
    public async Task OrdersToday_WithNoOrders_RepliesNoOrders()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.Contains(_sentMessages, m => m.Contains("Aaj koi order nahi aaya"));
    }

    [Fact]
    public async Task FreeformMessage_WhenAiFindsNoOrder_AsksClarification()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = false,
                ClarificationQuestion = "Mujhe samajh nahi aaya 🤔 Kya aap:",
                ClarificationOptions = { "Naya order add karna chahte hain", "Kisi order ka status update karna chahte hain" }
            });

        await engine.HandleIncomingMessageAsync(Phone, "wo waala order kal tak bhej dena", default);

        Assert.Contains(_sentMessages, m => m.Contains("Mujhe samajh nahi aaya"));

        var session = await db.Sessions.FirstAsync();
        Assert.Equal(ConversationState.AwaitingClarificationChoice, session.State);
    }

    [Fact]
    public async Task ResetAccount_WorksEvenWhenStuckInClarificationMenu()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = false,
                ClarificationQuestion = "Kya aap:",
                ClarificationOptions = { "Haan", "Nahi" }
            });
        await engine.HandleIncomingMessageAsync(Phone, "gibberish message", default);
        Assert.Equal(ConversationState.AwaitingClarificationChoice, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "Reset account", default);
        Assert.Equal(ConversationState.AwaitingResetConfirmation, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Equal(ConversationState.OnboardingLanguage, (await db.Sessions.FirstAsync()).State);
    }

    [Theory]
    [InlineData("English", "english")]
    [InlineData("اردو", "urdu_script")]
    [InlineData("Roman Urdu", "roman_urdu")]
    public async Task FirstTouch_ShowsIntroThenSavesChosenLanguage(string reply, string expected)
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "hi", default);
        Assert.Contains(_sentMessages, m => m.Contains("Order Assistant"));
        Assert.Equal(ConversationState.OnboardingLanguage, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, reply, default);

        var seller = await db.Sellers.FirstAsync();
        Assert.Equal(expected, seller.PreferredLanguage);
        Assert.Equal(ConversationState.OnboardingBusinessName, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task FreeformOrder_FullyResolved_GoesStraightToConfirmation_ThenSavesOnYes()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Sara",
                    Phone = "03009876543",
                    Address = "Gulberg Lahore",
                    Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } }
                }
            });

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 kurti, 03009876543, Gulberg Lahore", default);
        Assert.Contains(_sentMessages, m => m.Contains("Confirm order") && m.Contains("Sara"));

        _sentMessages.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Contains(_sentMessages, m => m.Contains("Order saved as PENDING"));
        var order = await db.Orders.Include(o => o.Items).Include(o => o.Customer).SingleAsync();
        Assert.Equal("Sara", order.Customer!.Name);
        Assert.Equal(1800m, order.Total);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public async Task MarkStatus_Shipped_ThenUndo_RevertsToPending()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Bilal",
                    Phone = "03001112222",
                    Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } }
                }
            });
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 1 kurti, 03001112222", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var order = await db.Orders.SingleAsync();
        _sentMessages.Clear();

        await engine.HandleIncomingMessageAsync(Phone, $"mark {order.Id} shipped", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(OrderStatus.Shipped, order.Status);

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);
        await db.Entry(order).ReloadAsync();
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public async Task CreateDiscount_ThenAppliedOrder_TotalsReflectPercentOff()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "create discount: EID10, 10 percent", default);
        _sentMessages.Clear();

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Ayesha",
                    Phone = "03001234567",
                    DiscountCode = "EID10",
                    Items = { new AiOrderItemDraft { ProductName = "Lawn Suit", MatchedCatalogProductName = "Lawn Suit", Quantity = 2 } }
                }
            });

        await engine.HandleIncomingMessageAsync(Phone, "new order: Ayesha, 2x Lawn Suit, EID10", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var order = await db.Orders.SingleAsync();
        Assert.Equal(7000m, order.Subtotal);
        Assert.Equal(700m, order.DiscountAmount);
        Assert.Equal(6300m, order.Total);
    }

    [Fact]
    public async Task UnknownProduct_PromptsForPrice_ThenAddsToCatalogAndConfirmsOrder()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardSellerAsync(db);
        var engine = CreateEngine(db);

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Hina",
                    Phone = "03211234567",
                    Items = { new AiOrderItemDraft { ProductName = "Sharara", MatchedCatalogProductName = null, Quantity = 1 } }
                }
            });

        await engine.HandleIncomingMessageAsync(Phone, "Hina, 1 sharara, 03211234567", default);
        Assert.Contains(_sentMessages, m => m.Contains("Sharara") && m.Contains("nahi mila"));

        _sentMessages.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "1", default);
        Assert.Contains(_sentMessages, m => m.Contains("price kya hai"));

        _sentMessages.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "4200", default);
        Assert.Contains(_sentMessages, m => m.Contains("Confirm order"));

        var product = await db.Products.SingleAsync(p => p.Name == "Sharara");
        Assert.Equal(4200m, product.Price);
    }

    public void Dispose() => _dbFactory.Dispose();
}
