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
    public async Task MidOnboardingCommand_IsDeferredNotExecuted()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
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

        Assert.Contains(_sentMessages, m => m.Contains("Aaj koi order nahi hai"));
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
