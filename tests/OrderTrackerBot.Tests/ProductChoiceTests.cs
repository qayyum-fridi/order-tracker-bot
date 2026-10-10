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
