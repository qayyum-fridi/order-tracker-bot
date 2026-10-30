using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// P1 of docs/design-learned-utterances.md: unplaced messages are stored masked, and the report reads them. No reply changes.
public class UnplacedCaptureTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();

    public UnplacedCaptureTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private ConversationEngine Engine(AppDbContext db, bool capture, string salt = "test-salt") =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ShortcutButtons = false, UnplacedCapture = capture, UnplacedCaptureSalt = salt });

    private static async Task SeedAsync(AppDbContext db)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        db.Customers.Add(new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001234567" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UnplacedMessage_IsStoredMasked_WithoutNamesOrPhones()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db, capture: true).HandleIncomingMessageAsync(Phone, "chai pani Sara ko 500 ka bhejo 03001234567", default);

        var row = await db.UnplacedUtterances.SingleAsync();
        Assert.Equal("chai pani {name} ko {n} ka bhejo {phone}", row.MaskedText);
        Assert.Equal("freeform_unclear", row.Stage);
        Assert.DoesNotContain("sara", row.MaskedText);
        Assert.DoesNotContain("03001234567", row.MaskedText);
    }

    [Fact]
    public async Task Capture_IsOff_ByDefault_AndWithoutASalt()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db, capture: false).HandleIncomingMessageAsync(Phone, "chai pani", default);
        await Engine(db, capture: true, salt: "").HandleIncomingMessageAsync(Phone, "chai pani", default);

        Assert.Equal(0, await db.UnplacedUtterances.CountAsync());
    }

    [Fact]
    public async Task Capture_DoesNotChangeTheReply()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var replies = new List<string>();
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => replies.Add(t)).Returns(Task.CompletedTask);

        await Engine(db, capture: false).HandleIncomingMessageAsync(Phone, "chai pani", default);
        var withoutCapture = replies.ToList();
        replies.Clear();
        await Engine(db, capture: true).HandleIncomingMessageAsync(Phone, "chai pani", default);

        Assert.Equal(withoutCapture, replies);
    }

    [Fact]
    public async Task Report_CountsByStage_AndPurgeRemovesOldRows()
    {
        using var db = _dbFactory.CreateContext();
        var now = DateTime.UtcNow;
        db.UnplacedUtterances.Add(new UnplacedUtterance { SellerHash = "h1", MaskedText = "menu dikhao", Stage = "freeform_unclear", CreatedAt = now });
        db.UnplacedUtterances.Add(new UnplacedUtterance { SellerHash = "h1", MaskedText = "menu dikhao", Stage = "freeform_unclear", CreatedAt = now });
        db.UnplacedUtterances.Add(new UnplacedUtterance { SellerHash = "h2", MaskedText = "stock dikhao", Stage = "voice_reject", CreatedAt = now });
        db.UnplacedUtterances.Add(new UnplacedUtterance { SellerHash = "h2", MaskedText = "purana", Stage = "voice_reject", CreatedAt = now.AddDays(-40) });
        await db.SaveChangesAsync();

        var report = await UnplacedReport.BuildAsync(db, 7, now.AddSeconds(1), default);
        Assert.Equal(3, report.Total);
        Assert.Equal(2, report.Stages.Single(s => s.Key == "freeform_unclear").Count);
        Assert.Equal("menu dikhao", report.TopMaskedTexts[0].Key);

        var purged = await UnplacedReport.PurgeOlderThanAsync(db, 30, now, default);
        Assert.Equal(1, purged);
    }

    public void Dispose() => _dbFactory.Dispose();
}
