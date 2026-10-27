using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Export;
using Xunit;

namespace OrderTrackerBot.Tests;

public class UnitSuggestionTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _texts = new();

    public UnitSuggestionTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _texts.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _texts.Add(b)).Returns(Task.CompletedTask);
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine CreateEngine(OrderTrackerBot.Infrastructure.Persistence.AppDbContext db) =>
        new(db, new Mock<IAiOrderAssistant>().Object, _sender.Object, new Mock<IFounderAlertNotifier>().Object, importReader: new ImportFileReader());

    [Theory]
    [InlineData("set", "set")]
    [InlineData("Pairs", "pair")]
    [InlineData("ml", "ml")]
    [InlineData("box", "box")]
    [InlineData("bori", "bag")]
    [InlineData("carton", "carton")]
    [InlineData("tola", "tola")]
    [InlineData("feet", "ft")]
    [InlineData("roll", "roll")]
    [InlineData("bundle", "bundle")]
    [InlineData("tray", "tray")]
    [InlineData("plate", "plate")]
    public void NewUnitWords_NormalizeToTheirStoredName(string word, string expected) =>
        Assert.Equal(expected, CommandParser.NormalizeUnit(word));

    [Fact]
    public void EveryUnitAnIndustryListsIsOneTheParserAccepts()
    {
        foreach (var profile in IndustryProfiles.All.Append(IndustryProfiles.General))
            foreach (var unit in profile.Units)
                Assert.True(CommandParser.NormalizeUnit(unit) == unit, $"{profile.Key}: \"{unit}\" is not a unit the parser stores");
    }

    [Fact]
    public void ClothingAndTolaUnits_AreInTheirIndustryLists()
    {
        Assert.Contains("set", IndustryProfiles.ByKey("clothing").Units);
        Assert.Contains("pair", IndustryProfiles.ByKey("clothing").Units);
        Assert.Contains("tola", IndustryProfiles.ByKey("beauty").Units);
    }

    [Fact]
    public void ProductLine_WithANewUnit_IsSavedWithItsUnit()
    {
        var parsed = CommandParser.TryParse("Lawn Suit 3 set - 9000");

        Assert.NotNull(parsed?.Product);
        Assert.Equal("set", parsed!.Product!.UnitType);
        Assert.Equal(3, parsed.Product.UnitQty);
    }

    [Fact]
    public async Task NewProduct_SuggestsTheIndustryUnits_AndAnExistingOneDoesNot()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        var seller = await db.Sellers.FirstAsync();
        seller.BusinessType = "Clothing";
        await db.SaveChangesAsync();
        _texts.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "Lawn Suit - 3500", default);
        Assert.Contains(_texts, t => t.Contains("Agar unit piece nahi") && t.Contains("set, pair"));

        _texts.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "Lawn Suit - 3500", default);
        Assert.DoesNotContain(_texts, t => t.Contains("Agar unit piece nahi"));
    }
}
