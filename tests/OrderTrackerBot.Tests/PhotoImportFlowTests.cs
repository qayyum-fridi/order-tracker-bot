using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Photo import: "photo customers|products|wages" makes the next photo read as that kind without a caption, shown for YES before anything is saved.
/// One choice covers up to five photos within ten minutes; a photo with its own caption always wins over the choice.
/// </summary>
public class PhotoImportFlowTests : IDisposable
{
    private const string Phone = "923001234567";
    private static readonly string[] Kinds = { "customer", "wages", "products" };

    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();
    private readonly List<string> _sent = new();

    public PhotoImportFlowTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body))
            .Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear" });
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine Engine(AppDbContext db, TimeProvider? clock = null) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: _media.Object, clock: clock);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db, TimeProvider? clock = null)
    {
        var engine = Engine(db, clock);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "Lawn Suit - 3500", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        _sent.Clear();
        return engine;
    }

    private void SetupPhoto(string id) =>
        _media.Setup(m => m.DownloadAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((new byte[3000], "image/jpeg"));

    /// <summary>Any kind of reading returns the same usable details, so the step built from it depends only on the kind.</summary>
    private void ReadAnyKind() =>
        _ai.Setup(a => a.ReadPictureAsync(It.IsAny<string>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AiPictureReading
            {
                Name = "Sara", Phone = "03001234567", Amount = 800, Note = "petrol",
                Products = { new AiPictureProduct { Name = "Silk Dupatta", Price = 900 } },
            });

    private static async Task<(int Customers, int Products, int Wages)> CountsAsync(AppDbContext db) =>
        (await db.Customers.CountAsync(), await db.Products.CountAsync(), await db.WageEntries.CountAsync());

    [Theory]
    [InlineData("photo customers", "customer", "customer add Sara")]
    [InlineData("photo wages", "wages", "wages Sara 800")]
    [InlineData("photo products", "products", "Silk Dupatta - 900")]
    public async Task ChosenKind_ReadsTheNextPhotoAsThatKind_AndSavesOnlyAfterYes(string choice, string kind, string expectedStep)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        SetupPhoto("kind-1");
        ReadAnyKind();
        await engine.HandleIncomingMessageAsync(Phone, choice, default);
        var before = await CountsAsync(db);
        _sent.Clear();

        await engine.HandleImageMessageAsync(Phone, "kind-1", null);

        _ai.Verify(a => a.ReadPictureAsync(kind, It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Once);
        foreach (var other in Kinds.Where(k => k != kind))
            _ai.Verify(a => a.ReadPictureAsync(other, It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(_sent, m => m.Contains(expectedStep) && m.Contains("YES"));
        Assert.Equal(before, await CountsAsync(db));                       // shown, not saved

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        var after = await CountsAsync(db);
        Assert.Equal(kind == "customer" ? 1 : 0, after.Customers - before.Customers);
        Assert.Equal(kind == "products" ? 1 : 0, after.Products - before.Products);
        Assert.Equal(kind == "wages" ? 1 : 0, after.Wages - before.Wages);
    }

    [Fact]
    public async Task PhotoWithItsOwnCaption_IsReadAsTheCaptionSays_NotAsTheChosenKind()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        SetupPhoto("kind-cap");
        ReadAnyKind();
        await engine.HandleIncomingMessageAsync(Phone, "photo customers", default);

        await engine.HandleImageMessageAsync(Phone, "kind-cap", "expense");

        _ai.Verify(a => a.ReadPictureAsync("expense", It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Once);
        _ai.Verify(a => a.ReadPictureAsync("customer", It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(9, true)]     // inside the ten-minute window
    [InlineData(11, false)]   // after it: the photo is not read as a customer list
    public async Task ChosenKind_ExpiresAfterTenMinutes(int minutes, bool readAsKind)
    {
        using var db = _dbFactory.CreateContext();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        var engine = await OnboardAsync(db, clock);
        SetupPhoto("kind-window");
        ReadAnyKind();
        await engine.HandleIncomingMessageAsync(Phone, "photo customers", default);
        clock.Advance(TimeSpan.FromMinutes(minutes));

        await engine.HandleImageMessageAsync(Phone, "kind-window", null);

        _ai.Verify(a => a.ReadPictureAsync("customer", It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), readAsKind ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task ChosenKind_CoversFivePhotos_ThenTheSixthIsNotReadAsThatKind()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var reads = 0;
        _ai.Setup(a => a.ReadPictureAsync("customer", It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AiPictureReading { Name = new[] { "Asma", "Bano", "Chand", "Dua", "Eman", "Fozia" }[reads++], Phone = "03001234567" });
        for (var i = 1; i <= 6; i++) SetupPhoto($"pg-{i}");
        await engine.HandleIncomingMessageAsync(Phone, "photo customers", default);

        for (var i = 1; i <= 5; i++)
            await engine.HandleImageMessageAsync(Phone, $"pg-{i}", null);

        // all five pages are in one YES list
        Assert.Contains(_sent, m => m.Contains("Asma") && m.Contains("Eman") && m.Contains("YES"));
        Assert.Equal(5, reads);

        await engine.HandleImageMessageAsync(Phone, "pg-6", null);

        _ai.Verify(a => a.ReadPictureAsync("customer", It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        _ai.Verify(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PhotoOrdersChoice_ReadsTheNextPhotoAsAnOrder_NotAsAListKind()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        SetupPhoto("kind-order");
        ReadAnyKind();
        await engine.HandleIncomingMessageAsync(Phone, "photo orders", default);

        await engine.HandleImageMessageAsync(Phone, "kind-order", null);

        foreach (var kind in Kinds)
            _ai.Verify(a => a.ReadPictureAsync(kind, It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Never);
        _ai.Verify(a => a.AnalyzeImageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<AiImageInput>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
