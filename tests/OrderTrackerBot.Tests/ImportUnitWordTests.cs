using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Export;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>A product file whose Unit column says "Box", "Boxes", "Bottle"... imports the row with the standard unit word.</summary>
public class ImportUnitWordTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();

    public ImportUnitWordTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine CreateEngine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: _media.Object, exportWriter: new ExportXlsxWriter(), importReader: new ImportFileReader());

    private async Task OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "Lawn Suit - 3500", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
    }

    private static byte[] ProductWorkbook(string unitWord)
    {
        var sheet = new ExportSheet("Catalog", new[]
        {
            new ExportColumn("Product", typeof(string)), new ExportColumn("Price", typeof(string)), new ExportColumn("Unit", typeof(string))
        }, new List<object?[]> { new object?[] { "Chadar", "1200", unitWord } });
        return new ExportXlsxWriter().WriteXlsx(new[] { sheet });
    }

    [Theory]
    [InlineData("Box", "box")]
    [InlineData("Boxes", "box")]
    [InlineData("Bottle", "bottle")]
    [InlineData("Bottles", "bottle")]
    [InlineData("Pouch", "pouch")]
    [InlineData("Packets", "pack")]
    [InlineData("Pkt", "pack")]
    [InlineData("Dozen", "dozen")]
    public async Task AUnitWord_ImportsTheRow_WithItsStandardUnit(string unitWord, string expectedUnit)
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        _media.Setup(m => m.DownloadAsync("m1", It.IsAny<CancellationToken>())).ReturnsAsync((ProductWorkbook(unitWord), XlsxMime));

        await CreateEngine(db).HandleDocumentMessageAsync(Phone, "m1", "products.xlsx", XlsxMime);
        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "Yes", default);

        var chadar = await db.Products.SingleAsync(p => p.Name == "Chadar");
        Assert.Equal(expectedUnit, chadar.UnitType);
    }
}
