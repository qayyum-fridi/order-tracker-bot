using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class StateEscapeSafetyTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public StateEscapeSafetyTests()
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

    private void AiSays(AiMessageAnalysis analysis) =>
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(analysis);

    private void UnknownProductOrder() => AiSays(new AiMessageAnalysis
    {
        Intent = "new_order", IsOrderAttempt = true,
        Order = new AiOrderDraft { CustomerName = "Yusrah", Phone = "03457278990", Items = { new AiOrderItemDraft { ProductName = "Lawn", Quantity = 2 } } }
    });

    [Theory]
    [InlineData("cancel mat karna")]
    [InlineData("Cancel Fashion Boutique")]
    [InlineData("cancel nahi karna order")]
    public async Task NegatedOrNamedCancel_DoesNotClearTheDraft(string text)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        UnknownProductOrder();
        await engine.HandleIncomingMessageAsync(Phone, "Yusrah 03457278990 2 lawn order place karo", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, text, default);

        Assert.DoesNotContain(_sent, m => m.Contains("order cancel kar diya"));
    }

    [Theory]
    [InlineData("khatam karo")]
    [InlineData("shuru se karenge")]
    [InlineData("chhoro yaar")]
    public async Task RomanUrduEscape_OpeningTheMessage_ClearsTheDraft(string text)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        UnknownProductOrder();
        await engine.HandleIncomingMessageAsync(Phone, "Yusrah 03457278990 2 lawn order place karo", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, text, default);

        Assert.Contains(_sent, m => m.Contains("order cancel kar diya"));
    }

    [Fact]
    public async Task CommandWhilePriceIsPending_RunsTheCommand()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        AiSays(new AiMessageAnalysis { Intent = "add_products", NewProducts = { new AiNewProduct { Name = "Lawn Suit" } } });
        await engine.HandleIncomingMessageAsync(Phone, "lawn suit product hai", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        Assert.Contains(_sent, m => m.Contains("Aaj koi order nahi aaya"));
        Assert.DoesNotContain(_sent, m => m.Contains("sale price"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
