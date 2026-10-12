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

    public void Dispose() => _dbFactory.Dispose();
}
