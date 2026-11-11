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
        ["product"] = "Shirt", ["reason"] = "damaged", ["method"] = "jazzcash", ["account"] = "03001234567", ["tracking_no"] = "TCS123",
        ["query"] = "1203", ["customer"] = "Sara", ["name"] = "Sara", ["price"] = "1500", ["cost"] = "300", ["stock"] = "10",
        ["category"] = "Shirts", ["supplier"] = "Shah Alam", ["code"] = "EID10", ["value"] = "10%", ["tier_price"] = "450", ["group"] = "B",
        ["size"] = "25", ["page"] = "2", ["phone"] = "03001234567", ["address"] = "Model Town", ["city"] = "Lahore",
        ["email"] = "sara@mail.com", ["message"] = "naya stock aaya", ["courier"] = "TCS", ["id"] = "12", ["text"] = "Lahore Branch",
        ["paper"] = "80mm", ["label_size"] = "a6", ["industry"] = "kiryana", ["days"] = "30", ["old_price"] = "500", ["new_price"] = "550",
        ["percent"] = "10", ["period"] = "last week", ["place"] = "Model Town", ["entity"] = "customers",
        ["attribute"] = "fabric", ["attribute_value"] = "cotton", ["language"] = "urdu", ["menu_name"] = "orders", ["role"] = "driver", ["tax_id"] = "1234567-8", ["tiers"] = "1kg=320, 10kg=300", ["topic"] = "order save nahi ho raha", ["what"] = "customers", ["action"] = "converted",
    };

    // Kinds that are deliberately not catalog entries. Everything else a seller can type has one, so a new CommandKind fails
    // EveryCommandKind_HasAnIntent_OrIsListedAsExcluded until it gets an entry (or a reason is written here).
    private static readonly HashSet<string> NotInCatalog = new()
    {
        "Start", "Greeting",                           // the first message of a chat, handled by onboarding
        "GuideLater", "StatusPicker", "PhotoKind", "PaymentMethodPrompt", // answers to a question the bot just asked (taps)
        "DetailedForm", "FuzzyStatusUpdate",           // built by the parser from a longer sentence, not a typed line
        "AddProductsBulk", "ImportCatalogSheet",       // the whole message is the data (a pasted list, a file link)
        "CreateLoyalty",                               // loyalty is off in the MVP cut; loyalty.create explains it
        "ForwardedQuery",                              // the whole message is the buyer's question and who asked it, no typed line to template
        "Notice",                                      // a template answer the parser picks for phrases it must not act on
    };

    [Fact]
    public void EveryCommandKind_HasAnIntent_OrIsListedAsExcluded()
    {
        var covered = IntentCatalog.All.Select(i => i.Kind).ToHashSet();
        var missing = Enum.GetNames<CommandKind>().Where(k => !covered.Contains(k) && !NotInCatalog.Contains(k)).ToList();
        Assert.Empty(missing);
        Assert.Empty(NotInCatalog.Where(k => covered.Contains(k))); // an excluded kind that got an entry should leave the list
    }

    [Fact]
    public void EveryExample_OfEveryNewEntry_HasRomanUrduEnglishAndUrduScript()
    {
        var ids = new[] { "staff.add", "staff.list", "staff.remove", "guide.howto", "support.contact" };
        foreach (var id in ids)
        {
            var examples = IntentCatalog.Find(id)!.Examples;
            Assert.Contains(examples, e => e.Any(c => c is >= '؀' and <= 'ۿ')); // Urdu script
            Assert.Contains(examples, e => e.Any(char.IsLetter) && e.All(c => c < '؀')); // Latin letters (Roman Urdu or English)
        }
    }

    [Fact]
    public void Closest_NamesTheStaffEntry_ForAHowToAddQuestion_InAllThreeScripts()
    {
        Assert.Equal("staff.add", IntentCatalog.Closest("how do we add a staff member")[0].Intent.Id);
        Assert.Equal("staff.add", IntentCatalog.Closest("mulazim kaise add karun")[0].Intent.Id);
        Assert.Equal("staff.add", IntentCatalog.Closest("ملازم کیسے شامل کروں")[0].Intent.Id);
    }

    [Fact]
    public void SampleCommand_ShowsAWorkedExample_ForStaffAdd()
    {
        Assert.Equal("staff add Ali 03001234567 driver", IntentCatalog.SampleCommand(IntentCatalog.Find("staff.add")!));
    }

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
        var built = IntentCatalog.Build(IntentCatalog.Find("product.create")!, new Dictionary<string, string> { ["name"] = "Shirt" });

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
            new Dictionary<string, string> { ["name"] = "Shirt", ["price"] = price });

        Assert.Null(built.Command);
        Assert.Contains("price", built.Missing);
    }

    [Fact]
    public void NumberSlot_AcceptsThousandsCommas()
    {
        var built = IntentCatalog.Build(IntentCatalog.Find("product.create")!,
            new Dictionary<string, string> { ["name"] = "Shirt", ["price"] = "1,500" });

        Assert.Equal("Shirt - 1500", built.Command);
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
