using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Application.Templates;
using Xunit;

namespace OrderTrackerBot.Tests;

// The intent catalog (Intents/intents.json): every template must become a command the deterministic parser reads, as the kind it declares.
public class IntentCatalogTests
{
    // One realistic value for every slot name used in the catalog. A new slot without a sample fails EveryIntent_HasSampleValues_ForEverySlot.
    private static readonly Dictionary<string, string> SampleSlots = new()
    {
        ["order"] = "12", ["order_ids"] = "1 2 3", ["status"] = "shipped", ["amount"] = "500", ["note"] = "gift wrap", ["qty"] = "2",
        ["product"] = "Kurti", ["reason"] = "damaged", ["method"] = "jazzcash", ["account"] = "03001234567", ["tracking_no"] = "TCS123",
        ["query"] = "1203", ["customer"] = "Sara", ["name"] = "Sara", ["price"] = "1500", ["cost"] = "300", ["stock"] = "10",
        ["category"] = "Shirts", ["supplier"] = "Shah Alam", ["code"] = "EID10", ["value"] = "10%", ["tier_price"] = "450", ["group"] = "B",
        ["size"] = "25", ["page"] = "2", ["phone"] = "03001234567", ["address"] = "Model Town", ["city"] = "Lahore",
        ["email"] = "sara@mail.com", ["message"] = "naya stock aaya", ["courier"] = "TCS", ["id"] = "12", ["text"] = "Lahore Branch",
        ["paper"] = "80mm", ["label_size"] = "a6", ["industry"] = "kiryana", ["days"] = "30", ["old_price"] = "500", ["new_price"] = "550",
        ["percent"] = "10", ["period"] = "last week", ["place"] = "Model Town",
    };

    public static IEnumerable<object[]> Intents() => IntentCatalog.All.Select(i => new object[] { i.Id });

    [Fact]
    public void Catalog_HasUniqueIds_AndEveryIntentHasExamples()
    {
        Assert.Equal(IntentCatalog.All.Count, IntentCatalog.All.Select(i => i.Id).Distinct().Count());
        Assert.All(IntentCatalog.All, i => Assert.NotEmpty(i.Examples));
    }

    [Fact]
    public void EveryIntent_HasSampleValues_ForEverySlot()
    {
        var missing = IntentCatalog.All.SelectMany(i => i.Slots.Select(s => s.Name)).Where(n => !SampleSlots.ContainsKey(n)).Distinct().ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Intents))]
    public void EveryIntent_BuildsACommandThatTheParserReadsAsItsKind(string id)
    {
        var intent = IntentCatalog.Find(id)!;
        var built = IntentCatalog.Build(intent, SampleSlots);

        Assert.NotNull(built.Command);
        var parsed = CommandParser.TryParse(built.Command!);
        Assert.True(parsed is not null, $"{id} built \"{built.Command}\", which the parser does not read");
        Assert.True(parsed!.Kind.ToString() == intent.Kind, $"{id} built \"{built.Command}\" parsed as {parsed.Kind}, declared {intent.Kind}");
    }

    [Theory]
    [MemberData(nameof(Intents))]
    public void EveryIntent_CanBeShownAsAHowTo(string id)
    {
        var example = IntentCatalog.Example(IntentCatalog.Find(id)!);
        Assert.False(string.IsNullOrWhiteSpace(MessageTemplates.Get("intent.howto", example)));
    }

    [Fact]
    public void MissingRequiredSlot_GivesNoCommand_AndNamesTheSlot()
    {
        var built = IntentCatalog.Build(IntentCatalog.Find("product.create")!, new Dictionary<string, string> { ["name"] = "Kurti" });

        Assert.Null(built.Command);
        Assert.Equal(new[] { "price" }, built.Missing);
        Assert.Equal("[name] - [price]", IntentCatalog.Example(IntentCatalog.Find("product.create")!));
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
        var built = IntentCatalog.Build(IntentCatalog.Find("customer.add")!, new Dictionary<string, string> { ["name"] = name });

        Assert.Null(built.Command);
    }

    [Fact]
    public void OptionalSlot_LeftOut_DoesNotLeaveAGap()
    {
        var built = IntentCatalog.Build(IntentCatalog.Find("ask.avg_daily")!, new Dictionary<string, string>());

        Assert.Equal("roz ki average bikri", built.Command);
    }

    [Fact]
    public void UnknownIntentId_IsNotFound()
    {
        Assert.Null(IntentCatalog.Find("order.delete_everything"));
    }
}
