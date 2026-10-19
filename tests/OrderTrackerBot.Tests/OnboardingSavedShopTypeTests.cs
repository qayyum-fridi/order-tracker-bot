using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>A shop type saved on the dashboard answers the setup question, so WhatsApp setup doesn't ask it again.</summary>
public class OnboardingSavedShopTypeTests : IDisposable
{
    private const string Phone = "923124891615";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();

    public OnboardingSavedShopTypeTests()
    {
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    public void Dispose() { }

    [Fact]
    public async Task Saved_shop_type_moves_setup_on_instead_of_asking_again()
    {
        using var db = _dbFactory.CreateContext();
        db.Sellers.Add(new Seller
        {
            WhatsAppPhoneNumber = Phone, BusinessName = "qayyum shop", BusinessType = "Cosmetics / Jewellery",
            OnboardingComplete = false, Session = new ConversationSession { State = ConversationState.OnboardingIndustry },
        });
        await db.SaveChangesAsync();

        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false, IndustryQuestion = true });
        await engine.HandleIncomingMessageAsync(Phone, "Business Setup", default);

        var seller = await db.Sellers.Include(s => s.Session).SingleAsync();
        Assert.Equal(ConversationState.OnboardingOptionalDetails, seller.Session!.State);
        Assert.Equal("Cosmetics / Jewellery", seller.BusinessType);
    }
}
