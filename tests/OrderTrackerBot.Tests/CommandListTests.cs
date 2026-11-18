using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>The "commands" cheat sheet: recognised in several wordings, shows the seller's own kind of shop, and every typed command in it still parses.</summary>
public class CommandListTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _sent = new();

    public CommandListTests() =>
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);

    [Theory]
    [InlineData("commands")]
    [InlineData("Commands")]
    [InlineData("command list")]
    [InlineData("basic commands")]
    [InlineData("commands dikhao")]
    [InlineData("show commands")]
    [InlineData("cheat sheet")]
    [InlineData("kya commands hain")]
    [InlineData("کمانڈز")]
    public void Parser_RecognisesTheCommandList(string text) => Assert.Equal(CommandKind.CommandList, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("commands ka order 5")]
    [InlineData("command mein kya likhun order ke liye")]
    public void Parser_IgnoresSentences(string text) => Assert.NotEqual(CommandKind.CommandList, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("category banao Shoes")]
    [InlineData("edit product: Shirt - 2000")]
    [InlineData("delete product Shirt")]
    [InlineData("stock Shirt 20")]
    [InlineData("stock Shirt +10")]
    [InlineData("catalog")]
    [InlineData("customer add Sara, Model Town, 03001234567")]
    [InlineData("Sara ka phone 03001234567")]
    [InlineData("order for Sara")]
    [InlineData("walkin Shirt 2")]
    [InlineData("order 12 advance 500")]
    [InlineData("order 12 delivery 300")]
    [InlineData("edit order 12")]
    [InlineData("mark 3 shipped")]
    [InlineData("mark 3 delivered")]
    [InlineData("mark 3 returned")]
    [InlineData("mark 3 paid")]
    [InlineData("cancel 3")]
    [InlineData("undo")]
    [InlineData("expense 500 packaging")]
    [InlineData("expenses today")]
    [InlineData("create discount: EID10 10%")]
    [InlineData("drawer open 5000")]
    [InlineData("cash in 200 note")]
    [InlineData("orders today")]
    [InlineData("aaj ka hisaab")]
    [InlineData("profit")]
    [InlineData("margin")]
    [InlineData("monthly net")]
    [InlineData("help")]
    [InlineData("menu")]
    [InlineData("guide")]
    public void EveryCommandInTheSheet_StillParses(string typed) => Assert.NotNull(CommandParser.TryParse(typed));

    [Fact]
    public async Task Commands_ShowsFourMessages_WithTheSellersOwnShopExamples()
    {
        using var db = _dbFactory.CreateContext();
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Bilal Store", BusinessType = "Kiryana", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } });
        await db.SaveChangesAsync();
        var engine = new ConversationEngine(db, Mock.Of<IAiOrderAssistant>(), _sender.Object, Mock.Of<IFounderAlertNotifier>());

        await engine.HandleIncomingMessageAsync(Phone, "commands", default);

        Assert.Equal(4, _sent.Count(m => m.Contains("Basic commands")));
        Assert.Contains(_sent, m => m.Contains("`Chini 5 kg - 950`"));
        Assert.Contains(_sent, m => m.Contains("stock Chini 5 kg 20"));
        Assert.DoesNotContain(_sent, m => m.Contains("{0}") || m.Contains("{4}"));
        Assert.All(_sent, m => Assert.True(m.Length < 1500, "each part stays short"));
    }

    [Theory]
    [InlineData("english")]
    [InlineData("urdu_script")]
    public void EveryShopsSheet_HasAHandWrittenTranslation(string language)
    {
        foreach (var shop in IndustryProfiles.All.Append(IndustryProfiles.General))
            foreach (var key in new[] { "cmdlist.001", "cmdlist.002", "cmdlist.003", "cmdlist.004" })
            {
                var text = MessageTemplates.Get(key, shop.MainLine, shop.FullProduct, shop.MainName, shop.Category, shop.OrderExample);
                Assert.True(MessageTranslations.TryTranslate(text, language, out var written), $"{shop.Key}/{key}/{language}");
                // Each part shows its own example: the product line in part 1, the order example in part 2.
                var example = key switch { "cmdlist.001" => shop.MainLine, "cmdlist.002" => shop.OrderExample, _ => null };
                if (example is not null) Assert.Contains(example, written);
            }
    }

    public void Dispose() => _dbFactory.Dispose();
}
