using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

// A message nothing placed gets up to three tappable catalog examples. Each tap must be a safe command that parses to its own action.
public class MissChoiceTests
{
    private readonly ITestOutputHelper _out;
    public MissChoiceTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("munafa kitna hua")]
    public void UnplacedMessage_GetsSafeTapExamples(string message)
    {
        var options = ConversationEngine.MissChoiceOptions(message);
        Assert.NotEmpty(options);
        Assert.True(options.Count <= 3);
        foreach (var option in options)
        {
            Assert.True(option.Label.Length <= 20, $"label too long: {option.Label}");
            Assert.Equal(option.Label, option.Value);
            var parsed = CommandParser.TryParse(option.Value);
            Assert.NotNull(parsed);
            Assert.DoesNotContain(parsed!.Kind, ActionRisk.RiskyCommands);
        }
    }

    [Fact]
    public void MunafaQuestion_OffersTheProfitReport() =>
        Assert.Contains(ConversationEngine.MissChoiceOptions("munafa kitna hua"), o => o.Value == "profit");

    [Fact]
    public void WeakMatch_GetsNoWrongTap() =>
        // Only common words ("kitna", "kya", "dikhao"): no tap rather than an unrelated one.
        Assert.Empty(ConversationEngine.MissChoiceOptions("aaj kitna kya dikhao"));

    [Fact]
    public void NonsenseWords_GetNoTapExamples() =>
        Assert.Empty(ConversationEngine.MissChoiceOptions("zzqx vwpt bnmk"));

    [Fact]
    public void RiskyWords_NeverBecomeATap()
    {
        var options = ConversationEngine.MissChoiceOptions("order 12 cancel kar do");
        foreach (var option in options)
            Assert.NotEqual(CommandKind.CancelOrder, CommandParser.TryParse(option.Value)?.Kind);
    }

    [Fact]
    public void SameMessage_GivesSameOptions_EveryTime()
    {
        var first = ConversationEngine.MissChoiceOptions("pending orders ka hisaab").Select(o => o.Value).ToList();
        var second = ConversationEngine.MissChoiceOptions("pending orders ka hisaab").Select(o => o.Value).ToList();
        Assert.Equal(first, second);
    }

    // Measurement, not a gate: how many everyday phrasings the parser places, how many get tap options, and how many get nothing.
    // Run with OTB_PHRASES=1 to print the numbers.
    [Fact]
    public void Measure_EverydayPhrasings_PrintsCoverage()
    {
        if (Environment.GetEnvironmentVariable("OTB_PHRASES") != "1") return;

        var phrases = new[]
        {
            "naya customer Sara Lahore 03001234567", "Sara ka address kya hai", "Sara ke orders dikhao", "Sara ne kitna kharid liya",
            "Shirt 1800 add karo", "Shirt ka stock 10 hai", "Shirt kitne bachi hain", "naya item Dupatta 600", "Shirt ki price badal do 1900",
            "12 number deliver ho gaya", "Sara ka order kahan hai", "12 ka address badlo", "order 12 mein ek Shirt aur",
            "is mahine kitna kamaya", "munafa kitna hua", "stock kam kya hai", "sab se zyada kya bika", "kal ki sale", "is hafte ki sale kitni",
            "aaj ka hisaab", "pending orders batao", "aaj ke orders", "catalog dikhao", "mere customers dikhao",
        };
        var placed = 0; var tapped = 0; var nothing = new List<string>();
        foreach (var p in phrases)
        {
            if (CommandParser.TryParse(p) is not null) { placed++; continue; }
            var offered = ConversationEngine.MissChoiceOptions(p);
            if (offered.Count > 0)
            {
                tapped++;
                _out.WriteLine($"  {p}  =>  {string.Join(" | ", offered.Select(o => o.Value))}");
            }
            else nothing.Add(p);
        }
        _out.WriteLine($"placed by parser: {placed}/{phrases.Length}; tap options: {tapped}; nothing: {nothing.Count}");
        foreach (var p in nothing) _out.WriteLine("  nothing: " + p);
    }
}
