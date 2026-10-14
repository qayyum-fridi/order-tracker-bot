using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using Xunit;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Tests;

public class IndustryProfileTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();

    public void Dispose() => _dbFactory.Dispose();

    public static IEnumerable<object[]> AllProfiles() => IndustryProfiles.All.Append(IndustryProfiles.General).Select(p => new object[] { p.Key });

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EveryExampleLine_IsAProductTheParserAccepts(string key)
    {
        var profile = IndustryProfiles.ByKey(key);
        foreach (var line in profile.ProductLines)
            Assert.True(CommandParser.TryParseProductLine(line, out ProductLine? _), $"{profile.Key}: \"{line}\" is not a product line");
        Assert.NotEmpty(profile.ProductLines.Where(l => l.Contains(',')));
        Assert.NotEmpty(profile.ProductLines.Where(l => !l.Contains(',')));
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void EveryExampleLine_ParsesToTheSameCommandAsTyped(string key)
    {
        foreach (var line in IndustryProfiles.ByKey(key).ProductLines)
            Assert.Equal(CommandKind.AddProduct, CommandParser.TryParse(line)!.Kind);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TitlesFitAWhatsAppListRow_AndTheRowIdIsACommand(string key)
    {
        var profile = IndustryProfiles.ByKey(key);
        Assert.InRange(profile.Title.Length, 1, 24);
        var cmd = CommandParser.TryParse($"industry {profile.Key}");
        Assert.Equal(CommandKind.Industry, cmd!.Kind);
        Assert.Equal(profile.Key, cmd.Text);
    }

    [Theory]
    [InlineData("Clothing", "clothing")]
    [InlineData("lawn boutique", "clothing")]
    [InlineData("kapray ka kaam", "clothing")]
    [InlineData("Kiryana store", "grocery")]
    [InlineData("Grocery", "grocery")]
    [InlineData("jewellery", "beauty")]
    [InlineData("Mobile accessories", "electronics")]
    [InlineData("Home bakery", "food")]
    [InlineData("Hardware shop", "hardware")]
    [InlineData("kuch aur", "general")]
    [InlineData(null, "general")]
    public void BusinessTypeText_ResolvesToAProfile(string? text, string key) => Assert.Equal(key, IndustryProfiles.Resolve(text).Key);

    [Fact]
    public async Task IndustryCommand_SetsBusinessType_AndAnswersWithThatShopsGuide()
    {
        var sent = new List<string>();
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => sent.Add(t)).Returns(Task.CompletedTask);
        using var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Bilal Mart", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(), sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false });

        await engine.HandleIncomingMessageAsync(Phone, "business type kiryana", default);

        Assert.Equal("Grocery / Kiryana", (await db.Sellers.FindAsync(seller.Id))!.BusinessType);
        var reply = string.Join("\n", sent);
        Assert.Contains("Sugar 5 kg - 500", reply);
        Assert.Contains("Tips", reply);
        Assert.Contains("data check", reply);

        sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "data check", default);
        Assert.Contains("Catalog abhi khali", string.Join("\n", sent));
        Assert.Contains("Sugar 5 kg - 500, cost 440", string.Join("\n", sent));
    }
}

public class CatalogViewTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    private readonly List<IReadOnlyList<string>> _buttons = new();
    private AppDbContext _db = null!;
    private ConversationEngine _engine = null!;
    private Seller _seller = null!;

    public void Dispose() => _dbFactory.Dispose();

    private async Task SetupAsync(params (string Name, string? Category)[] products)
    {
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, btn, _) => { _sent.Add(b); _buttons.Add(btn); }).Returns(Task.CompletedTask);
        _db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Bilal Mart", BusinessType = "Grocery / Kiryana", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        _db.Sellers.Add(_seller);
        await _db.SaveChangesAsync();
        var t = DateTime.UtcNow.AddDays(-products.Length);
        foreach (var (name, cat) in products)
            _db.Products.Add(new Product { SellerId = _seller.Id, Name = name, Price = 100, Category = cat, IsActive = true, CreatedAt = t = t.AddMinutes(1) });
        await _db.SaveChangesAsync();
        _engine = new ConversationEngine(_db, Mock.Of<IAiOrderAssistant>(), sender.Object, Mock.Of<IFounderAlertNotifier>(), features: new FeatureOptions { ShortcutButtons = false });
    }

    private async Task<string> AskAsync(string text)
    {
        _sent.Clear();
        await _engine.HandleIncomingMessageAsync(Phone, text, default);
        return string.Join("\n---\n", _sent);
    }

    [Theory]
    [InlineData("catalog next", "next")]
    [InlineData("catalog prev", "prev")]
    [InlineData("⬅️ catalog prev", "prev")]
    [InlineData("➡️ catalog next", "next")]
    [InlineData("catalog a-z", "az")]
    [InlineData("catalog az", "az")]
    [InlineData("catalog latest", "latest")]
    [InlineData("catalog page 3", "page:3")]
    public void ParsesCatalogViewCommands(string text, string action)
    {
        var cmd = CommandParser.TryParse(text);
        Assert.Equal(CommandKind.Catalog, cmd!.Kind);
        Assert.Equal(action, cmd.Text);
    }

    [Theory]
    [InlineData("catalog size 25", "size:25")]
    [InlineData("page size 50", "size:50")]
    [InlineData("per page 5", "size:5")]
    [InlineData("page 8", "page:8")]
    [InlineData("go to page 2", "page:2")]
    [InlineData("catalog last", "last")]
    [InlineData("last page", "last")]
    [InlineData("search chini", "q:chini")]
    [InlineData("catalog search lawn suit", "q:lawn suit")]
    [InlineData("find Sugar 5 kg", "q:Sugar 5 kg")]
    public void ParsesSearchSizeAndJump(string text, string action)
    {
        var cmd = CommandParser.TryParse(text);
        Assert.Equal(CommandKind.Catalog, cmd!.Kind);
        Assert.Equal(action, cmd.Text);
    }

    [Fact]
    public async Task Search_ShowsOnlyMatches_PagedAndKeptUntilReset()
    {
        await SetupAsync(Enumerable.Range(1, 30).Select(i => (i % 2 == 0 ? $"Chini {i:00}" : $"Atta {i:00}", i % 2 == 0 ? "Rashan" : "Flour")).ToArray());
        var r = await AskAsync("catalog search chini");
        Assert.Contains("Talash: \"chini\" — 15 nateeje (30 mein se)", r);
        Assert.Contains("Dikh rahe hain 1–10 / 15 · Page 1/2", r);
        Assert.DoesNotContain("Atta", r);
        Assert.Equal(new[] { "➡️ catalog next", "catalog reset" }, _buttons.Last());

        var p2 = await AskAsync("catalog next");
        Assert.Contains("Page 2/2", p2);
        Assert.Contains("Chini 30", p2);
        Assert.DoesNotContain("Atta", p2);

        var back = await AskAsync("catalog reset");
        Assert.Contains("Aapka Catalog (30 products)", back);
        Assert.Contains("Atta", back);
    }

    [Fact]
    public async Task Search_MatchesCategory_AndSeveralWords_AndSaysWhenNothingFound()
    {
        await SetupAsync(("Chini Safed", "Rashan"), ("Namak", "Rashan"), ("Soap", "Cleaning"));
        Assert.Contains("2 nateeje", await AskAsync("search rashan"));
        var two = await AskAsync("search chini safed");
        Assert.Contains("1 nateeje", two);
        Assert.Contains("Chini Safed", two);
        var none = await AskAsync("search xyzzy");
        Assert.Contains("koi product nahi mila", none);
        var near = await AskAsync("search Chinni Safed");
        Assert.Contains("Kya aap \"Chini Safed\"", near);
    }

    [Fact]
    public async Task PageSize_IsChosenByTheSeller_RememberedAndClamped()
    {
        await SetupAsync(Enumerable.Range(1, 60).Select(i => ($"Item {i:00}", (string?)null)).ToArray());
        var s25 = await AskAsync("catalog size 25");
        Assert.Contains("Ab har page par 25 products", s25);
        Assert.Contains("Page 1/3", s25);
        Assert.Contains("25 Item 25", s25);
        Assert.DoesNotContain("26 Item 26", s25);

        // remembered: a later plain "catalog" and next/prev keep using 25
        var again = await AskAsync("catalog");
        Assert.Contains("Page 1/3", again);
        Assert.Contains("26 Item 26", await AskAsync("catalog next"));

        Assert.Contains("50 set kar diya", await AskAsync("catalog size 500"));
        Assert.Contains("5 set kar diya", await AskAsync("catalog size 1"));
        Assert.Contains("Page 1/12", await AskAsync("catalog"));
    }

    [Fact]
    public async Task JumpToAPage_AndLast_AndOutOfRangeIsClamped()
    {
        await SetupAsync(Enumerable.Range(1, 100).Select(i => ($"Item {i:000}", (string?)null)).ToArray());
        var p8 = await AskAsync("catalog page 8");
        Assert.Contains("Page 8/10", p8);
        Assert.Contains("Dikh rahe hain 71–80 / 100", p8);
        Assert.Contains("71 Item 071", p8);
        Assert.Contains("Page 10/10", await AskAsync("catalog last"));
        Assert.Contains("Page 10/10", await AskAsync("page 99"));
        Assert.Contains("Page 3/10", await AskAsync("page 3"));
    }

    [Fact]
    public async Task NoCategory_StaysFlat_AndTellsHowToAddOne()
    {
        await SetupAsync(("Sugar", null), ("Rice", null));
        var reply = await AskAsync("catalog");
        Assert.Contains("Aapka Catalog (2 products)", reply);
        Assert.DoesNotContain("📂", reply);
        Assert.Contains("Category likhne se list category-wise", reply);
        Assert.Contains("Sugar 5 kg - 500, category rashan", reply);
    }

    [Fact]
    public async Task Categories_AndSubCategories_AreShownAsHeadings_NoCategoryLast()
    {
        await SetupAsync(("Chini", "Rashan > Cheeni"), ("Atta", "Rashan"), ("Namak", "Rashan > Masalay"), ("Soap", "Cleaning"), ("Mystery", null));
        var reply = await AskAsync("catalog");
        var order = new[] { "📂 *Cleaning*", "📂 *Rashan*", "▫️ *Cheeni*", "▫️ *Masalay*", "📂 *Bina category*" };
        var at = order.Select(o => reply.IndexOf(o, StringComparison.Ordinal)).ToList();
        Assert.All(at, i => Assert.True(i >= 0, reply));
        Assert.Equal(at.OrderBy(i => i).ToList(), at);
        Assert.True(reply.IndexOf("Atta", StringComparison.Ordinal) < reply.IndexOf("▫️ *Cheeni*", StringComparison.Ordinal), "main-category items come before its sub-categories");
        Assert.Contains("1 products ki category nahi", reply);
    }

    [Fact]
    public async Task Pagination_TenAPage_NextPrev_AndButtons()
    {
        await SetupAsync(Enumerable.Range(1, 25).Select(i => ($"Item {i:00}", (string?)null)).ToArray());
        var p1 = await AskAsync("catalog");
        Assert.Contains("Page 1/3", p1);
        Assert.Contains("1 Item 01", p1);
        Assert.Contains("10 Item 10", p1);
        Assert.DoesNotContain("11 Item 11", p1);
        Assert.Equal(new[] { "➡️ catalog next", "catalog a-z" }, _buttons.Last());

        var p2 = await AskAsync("catalog next");
        Assert.Contains("Page 2/3", p2);
        Assert.Contains("11 Item 11", p2);
        Assert.Equal(new[] { "⬅️ catalog prev", "➡️ catalog next", "catalog a-z" }, _buttons.Last());

        var p3 = await AskAsync("catalog next");
        Assert.Contains("25 Item 25", p3);
        Assert.Equal(new[] { "⬅️ catalog prev", "catalog a-z" }, _buttons.Last());

        var again = await AskAsync("catalog next");
        Assert.Contains("Page 3/3", again);

        var back = await AskAsync("catalog prev");
        Assert.Contains("Page 2/3", back);
        Assert.Contains("Page 1/3", await AskAsync("catalog page 1"));
    }

    [Fact]
    public async Task Sorting_Latest_AndAlphabetical_Toggle()
    {
        await SetupAsync(Enumerable.Range(1, 12).Select(i => ($"{(char)('Z' - i)}prod", (string?)null)).ToArray());
        var latest = await AskAsync("catalog latest");
        Assert.Contains("naya pehle", latest);
        Assert.True(latest.IndexOf("Oprod", StringComparison.Ordinal) < latest.IndexOf("Pprod", StringComparison.Ordinal)); // the last added (smallest letter) first
        Assert.Contains("catalog a-z", _buttons.Last());

        var az = await AskAsync("catalog a-z");
        Assert.Contains("A-Z", az);
        Assert.True(az.IndexOf("Oprod", StringComparison.Ordinal) < az.IndexOf("Pprod", StringComparison.Ordinal));
        Assert.Contains("catalog latest", _buttons.Last());

        var plain = await AskAsync("catalog");
        Assert.Contains("jaise add kiye", plain);
    }

    [Fact]
    public async Task Sorting_AppliesInsideEachCategory()
    {
        await SetupAsync(("Zeera", "Masala"), ("Adrak", "Masala"), ("Mirch", "Masala"));
        var az = await AskAsync("catalog a-z");
        Assert.True(az.IndexOf("Adrak", StringComparison.Ordinal) < az.IndexOf("Mirch", StringComparison.Ordinal));
        Assert.True(az.IndexOf("Mirch", StringComparison.Ordinal) < az.IndexOf("Zeera", StringComparison.Ordinal));
    }
}

public class IndustryQuestionTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly List<string> _sent = new();
    public void Dispose() => _dbFactory.Dispose();

    private async Task<(AppDbContext Db, ConversationEngine Engine, Seller Seller)> StartAsync()
    {
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _sent.Add(b)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, b, _, sections, _) => _sent.Add(b + "\n" + string.Join("|", sections.SelectMany(s => s.Rows).Select(r => r.Id)))).Returns(Task.CompletedTask);
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "", Session = new ConversationSession { State = ConversationState.OnboardingBusinessName } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(), sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ShortcutButtons = false, IndustryQuestion = true });
        return (db, engine, seller);
    }

    [Fact]
    public async Task ShopName_ThenAskedTheKindOfShop_AndATapSetsIt()
    {
        var (db, engine, seller) = await StartAsync();

        await engine.HandleIncomingMessageAsync(Phone, "Bilal Mart", default);
        var ask = string.Join("\n", _sent);
        Assert.Contains("dukaan kis qism ki hai", ask);
        Assert.Contains("industry grocery", ask);
        Assert.Contains("industry clothing", ask);
        Assert.Equal(ConversationState.OnboardingIndustry, (await db.Sessions.FirstAsync()).State);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "industry grocery", default);
        Assert.Equal("Grocery / Kiryana", (await db.Sellers.FindAsync(seller.Id))!.BusinessType);
        Assert.Equal(ConversationState.OnboardingOptionalDetails, (await db.Sessions.FirstAsync()).State);
        Assert.Contains("Dukaan ki qism: Grocery / Kiryana", string.Join("\n", _sent));
    }

    [Theory]
    [InlineData("kiryana store", "Grocery / Kiryana")]
    [InlineData("lawn boutique", "Kapray / Lawn / Boutique")]
    [InlineData("Hardware", "Hardware / Building")]
    public async Task ATypedAnswer_IsUnderstood(string typed, string title)
    {
        var (db, engine, seller) = await StartAsync();
        await engine.HandleIncomingMessageAsync(Phone, "Bilal Mart", default);
        await engine.HandleIncomingMessageAsync(Phone, typed, default);
        Assert.Equal(title, (await db.Sellers.FindAsync(seller.Id))!.BusinessType);
    }

    [Fact]
    public async Task AnUnknownAnswer_AsksAgain_AndNothingIsSaved()
    {
        var (db, engine, seller) = await StartAsync();
        await engine.HandleIncomingMessageAsync(Phone, "Bilal Mart", default);
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "xyzzy qqq", default);
        Assert.Contains("samajh nahi aaya", string.Join("\n", _sent));
        Assert.Null((await db.Sellers.FindAsync(seller.Id))!.BusinessType);
        Assert.Equal(ConversationState.OnboardingIndustry, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task TheCityLine_NoLongerOverwritesTheChosenKind()
    {
        var (db, engine, seller) = await StartAsync();
        await engine.HandleIncomingMessageAsync(Phone, "Bilal Mart", default);
        await engine.HandleIncomingMessageAsync(Phone, "industry grocery", default);
        await engine.HandleIncomingMessageAsync(Phone, "Karachi, Clothing, @bilal.mart", default);
        var saved = await db.Sellers.FindAsync(seller.Id);
        Assert.Equal("Grocery / Kiryana", saved!.BusinessType);
        Assert.Equal("Karachi", saved.City);
    }
}
