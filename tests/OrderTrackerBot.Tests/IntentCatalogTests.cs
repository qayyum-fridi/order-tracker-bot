using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Application.Templates;
using Xunit;

namespace OrderTrackerBot.Tests;

// The intent catalog (Intents/intents.json): every template must become a command the deterministic parser reads, and nothing risky.
public class IntentCatalogTests
{
    private static readonly Dictionary<string, string> SampleSlots = new()
    {
        ["name"] = "Sara", ["price"] = "1500", ["code"] = "EID10", ["value"] = "10%", ["amount"] = "500", ["note"] = "petrol"
    };

    public static IEnumerable<object[]> Intents() => IntentCatalog.All.Select(i => new object[] { i.Id });

    [Fact]
    public void Catalog_HasUniqueIds_AndEveryIntentHasExamples()
    {
        Assert.Equal(IntentCatalog.All.Count, IntentCatalog.All.Select(i => i.Id).Distinct().Count());
        Assert.All(IntentCatalog.All, i => Assert.NotEmpty(i.Examples));
    }

    [Theory]
    [MemberData(nameof(Intents))]
    public void EveryIntent_BuildsACommandThatTheParserReads(string id)
    {
        var intent = IntentCatalog.Find(id)!;
        var built = IntentCatalog.Build(intent, SampleSlots);

        Assert.NotNull(built.Command);
        var parsed = CommandParser.TryParse(built.Command!);
        Assert.True(parsed is not null, $"{id} built \"{built.Command}\", which the parser does not read");
        Assert.DoesNotContain(parsed!.Kind, ActionRisk.RiskyCommands);
    }

    [Theory]
    [MemberData(nameof(Intents))]
    public void EveryIntentWithAHelp_HasItsHowToReply(string id)
    {
        var help = IntentCatalog.Find(id)!.Help;
        if (help is not null) Assert.False(string.IsNullOrWhiteSpace(MessageTemplates.Get(help)));
    }

    [Fact]
    public void MissingRequiredSlot_GivesNoCommand_AndNamesTheSlot()
    {
        var product = IntentCatalog.Find("product.create")!;
        var built = IntentCatalog.Build(product, new Dictionary<string, string> { ["name"] = "Kurti" });

        Assert.Null(built.Command);
        Assert.Equal(new[] { "price" }, built.Missing);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12 rupay")]
    [InlineData("")]
    public void NumberSlot_MustBeAPlainNumber(string price)
    {
        var built = IntentCatalog.Build(IntentCatalog.Find("product.create")!,
            new Dictionary<string, string> { ["name"] = "Kurti", ["price"] = price });

        Assert.Null(built.Command);
        Assert.Contains("price", built.Missing);
    }

    [Fact]
    public void NumberSlot_AcceptsThousandsCommas()
    {
        var built = IntentCatalog.Build(IntentCatalog.Find("product.create")!,
            new Dictionary<string, string> { ["name"] = "Kurti", ["price"] = "1,500" });

        Assert.Equal("Kurti - 1500", built.Command);
    }

    [Theory]
    [InlineData("Sara {x}")]
    [InlineData("Sara\nDelete all")]
    public void TextSlot_WithBracesOrLineBreaks_IsRefused(string name)
    {
        var built = IntentCatalog.Build(IntentCatalog.Find("customer.create")!, new Dictionary<string, string> { ["name"] = name });

        Assert.Null(built.Command);
    }

    [Fact]
    public void UnknownIntentId_IsNotFound()
    {
        Assert.Null(IntentCatalog.Find("order.delete_everything"));
    }
}
