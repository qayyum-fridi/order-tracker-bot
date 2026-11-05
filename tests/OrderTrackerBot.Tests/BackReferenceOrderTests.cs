using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>
/// "wahi order" / "that order" / "وہی آرڈر" point to the order the seller touched last: saved, opened, or given a status change
/// (shipped, delivered, cancelled), for ten minutes.
/// </summary>
public class BackReferenceOrderTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();
    private readonly List<string> _sent = new();

    public BackReferenceOrderTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine Engine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, null, _media.Object);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = Engine(db);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "Lawn Suit - 3500", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        _sent.Clear();
        return engine;
    }

    /// <summary>Saves one order for a customer through the normal draft and YES, and returns its id.</summary>
    private async Task<int> PlaceOrderAsync(ConversationEngine engine, AppDbContext db, string name, string phone)
    {
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order",
                IsOrderAttempt = true,
                Order = new AiOrderDraft { CustomerName = name, Phone = phone, Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } } }
            });
        await engine.HandleIncomingMessageAsync(Phone, $"{name}, 1 kurti, {phone}", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        return (await db.Orders.OrderByDescending(o => o.Id).FirstAsync()).Id;
    }

    [Theory]
    [InlineData("mark {0} shipped")]
    [InlineData("mark {0} delivered")]
    [InlineData("cancel {0}")]
    public async Task StatusChange_MakesItTheOrderWahiPointsTo(string template)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var first = await PlaceOrderAsync(engine, db, "Sara", "03001111111");
        var second = await PlaceOrderAsync(engine, db, "Bilal", "03002222222");
        await engine.HandleIncomingMessageAsync(Phone, "wahi order", default);     // baseline: the last saved one
        Assert.Contains(_sent, m => m.Contains($"Order #{second}"));

        await engine.HandleIncomingMessageAsync(Phone, string.Format(template, first), default);
        if (template.StartsWith("cancel"))
            await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        // delivering a cash-on-delivery order asks "Cash collect ho gaya?" first; answer it so "wahi order" is the next message
        if (_sent.Any(m => m.Contains("Cash collect")))
            await engine.HandleIncomingMessageAsync(Phone, "no", default);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "wahi order", default);
        Assert.Contains(_sent, m => m.Contains($"Order #{first}"));
        Assert.DoesNotContain(_sent, m => m.Contains($"Order #{second}"));
    }

    [Theory]
    [InlineData("that order")]
    [InlineData("same order")]
    [InlineData("this order")]
    [InlineData("wahi order")]
    [InlineData("uska order")]
    [InlineData("وہی آرڈر")]
    [InlineData("اسی آرڈر")]
    [InlineData("وہی آرڈر دکھاؤ")]
    public async Task EnglishAndUrduWords_PointToTheLastOrder(string message)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await PlaceOrderAsync(engine, db, "Sara", "03001111111");
        var last = await PlaceOrderAsync(engine, db, "Bilal", "03002222222");
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, message, default);

        Assert.Contains(_sent, m => m.Contains($"Order #{last}"));
    }
}
