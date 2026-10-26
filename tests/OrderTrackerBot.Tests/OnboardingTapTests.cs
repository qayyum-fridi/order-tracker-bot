using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using Xunit;

namespace OrderTrackerBot.Tests;

// A non-technical seller should be able to get through setup by tapping, not by remembering words like "skip".
public class OnboardingTapTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();

    private ConversationEngine CreateEngine(OrderTrackerBot.Infrastructure.Persistence.AppDbContext db) =>
        new(db, new Mock<IAiOrderAssistant>().Object, _sender.Object, new Mock<IFounderAlertNotifier>().Object);

    [Fact]
    public async Task DetailsStep_OffersASkipButton_AndTappingItMovesOn()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);

        _sender.Verify(s => s.SendButtonsMessageAsync(Phone, It.Is<string>(b => b.Contains("order")),
            It.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "Skip" })), It.IsAny<CancellationToken>()), Times.Once);

        await engine.HandleIncomingMessageAsync(Phone, "Skip", default);

        Assert.Equal(ConversationState.OnboardingCatalogSize, db.Sessions.Single().State);
    }

    [Fact]
    public async Task CatalogStep_OffersAddProduct_AndItNeverDeadEnds()
    {
        var rows = new List<string>();
        var sent = new List<string>();
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, _, _, sections, _) => rows.AddRange(sections.SelectMany(s => s.Rows).Select(r => r.Id)))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => sent.Add(text))
            .Returns(Task.CompletedTask);

        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Skip" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);

        // The list shown after "how many products" must let the seller add a product.
        Assert.Contains("add product", rows);
        Assert.Equal(ConversationState.OnboardingAddProduct, db.Sessions.Single().State);

        // Tapping it again while adding products explains the format, and does not say "finish the catalog first".
        sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "add product", default);
        Assert.DoesNotContain(sent, m => m.Contains("pehle catalog complete"));
        Assert.Contains(sent, m => m.Contains("Water Bottle - 500"));
    }

    public void Dispose() => _dbFactory.Dispose();
}
