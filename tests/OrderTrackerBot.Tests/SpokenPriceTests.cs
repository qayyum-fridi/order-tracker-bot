using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class SpokenPriceTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public SpokenPriceTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
    }

    [Theory]
    [InlineData("paintis sau par", 3500)]
    [InlineData("پینتیس سو", 3500)]
    public void SpokenAmount_IsRecognised(string text, long amount) =>
        Assert.Contains(amount, SpokenNumbers.WordAmounts(text));

    [Fact]
    public async Task ProductPendingPrice_PricedBySpokenAmount_WithoutAskingAgain()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "add_products", NewProducts = { new AiNewProduct { Name = "Lawn Suit" } } });

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "lawn suit product hai, ad kar de paintis sau par", default);

        Assert.True(await db.Products.AnyAsync(p => p.Name == "Lawn Suit" && p.Price == 3500));
        Assert.DoesNotContain(_sent, m => m.Contains("sale price"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
