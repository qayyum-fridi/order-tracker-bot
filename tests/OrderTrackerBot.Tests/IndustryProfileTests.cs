using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using Xunit;

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
