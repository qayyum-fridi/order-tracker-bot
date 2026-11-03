using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Measures the voice REWRITE step (transcript -> typed steps) against the REAL model, per kind of sentence, so a change to the voice flow can be judged by a number.
/// It starts from transcripts, not audio: it does not measure the speech-to-text step. Skipped unless OTB_VOICE_EVAL=1 and OPENAI_API_KEY are set:
/// <c>OTB_VOICE_EVAL=1 OPENAI_API_KEY=... dotnet test --filter VoiceRewriteEval --logger "console;verbosity=detailed"</c>
/// Add the seller's real transcripts to <see cref="Cases"/>: a case passes when every <c>Has</c> text is in the steps, no <c>Not</c> text is, and (when <c>NoSteps</c>) there are none.
/// </summary>
public class VoiceRewriteEval
{
    private sealed record Case(string Kind, ConversationState State, string Transcript, string[] Has, string[] Not, bool NoSteps = false, string? LastBotMessage = null);

    private static readonly string[] None = Array.Empty<string>();
    private const ConversationState Idle = ConversationState.Idle;
    private const ConversationState Confirm = ConversationState.AwaitingOrderConfirmation;
    private const ConversationState Choice = ConversationState.AwaitingClarificationChoice;

    private static readonly Case[] Cases =
    {
        // an order said aloud
        new("order", Idle, "Ayesha ke liye teen Kurti aur do Dupatta, delivery do sau", new[] { "ayesha", "3", "kurti", "2", "dupatta", "200" }, None),
        new("order", Idle, "ek naya order aaya hai Hamira ka, usne das lawn suit mangwaye, aath sau ka lagaya hai", new[] { "hamira", "10", "lawn suit", "800" }, None),
        new("order", Idle, "Sara ko ek Polo Shirt white chahiye, phone zero teen zero zero ek do teen chaar paanch chhe saat", new[] { "sara", "polo shirt", "03001234567" }, None),
        // commands
        new("command", Idle, "aaj kitna bika", new[] { "hisaab" }, None),
        new("command", Idle, "order paanch shipped kar do", new[] { "5", "shipped" }, None),
        new("command", Idle, "Kurti ka stock bees kar do", new[] { "stock", "kurti", "20" }, None),
        new("command", Idle, "catalog dikhao", new[] { "catalog" }, None),
        new("command", Idle, "kharcha petrol paanch sau", new[] { "500", "petrol" }, None),
        new("command", Idle, "Ali ko paanch hazar tankhwah di", new[] { "ali", "5000" }, None),
        new("command", Idle, "pichle hafte ka hisaab dikhao", new[] { "hafte" }, None),
        new("command", Idle, "kya karun", new[] { "kya karun" }, None),
        new("command", Idle, "order teen cancel kar do", new[] { "cancel", "3" }, None),
        // numbers said in words
        new("numbers", Idle, "Lawn Suit teen hazar do sau ka hai", new[] { "lawn suit", "3200" }, None),
        new("numbers", Idle, "Kurti ki price ek hazar paanch sau kar do", new[] { "kurti", "1500" }, None),
        // the seller corrects himself while speaking
        new("self-correction", Idle, "Ayesha ko do Kurti nahi paanch Kurti bhejni hain", new[] { "ayesha", "5" }, new[] { "2 kurti" }),
        new("self-correction", Idle, "Kurti ki price do hazar nahi ek hazar paanch sau kar do", new[] { "1500" }, new[] { "2000" }),
        new("self-correction", Idle, "order teen shipped nahi delivered kar do", new[] { "3", "delivered" }, new[] { "shipped" }),
        // yes / no and a choice while the bot waits
        new("yes-no", Confirm, "او بھائی میں نے ہاں بولا تھا", new[] { "yes" }, new[] { "?" }),
        new("yes-no", Confirm, "oye pakka kar de yaar", new[] { "yes" }, None),
        new("yes-no", Confirm, "nahi rehne do", new[] { "no" }, new[] { "yes" }),
        new("yes-no", Confirm, "haan par delivery teen sau kar do", new[] { "300" }, None),
        new("choice", Choice, "doosra wala", new[] { "2" }, None, LastBotMessage: "Kaun sa client? 1 Ayesha Khan 2 Ayesha Raza"),
        new("choice", Choice, "pehla", new[] { "1" }, None, LastBotMessage: "Kaun sa client? 1 Ayesha Khan 2 Ayesha Raza"),
        // safety: nothing here may become an order or a command
        new("safety", Idle, "assalam o alaikum kya haal hai", None, None, NoSteps: true),
        new("safety", Idle, "aaj mausam bohat garam hai", None, None, NoSteps: true),
        new("safety", Idle, "oye bhai", None, None, NoSteps: true),
        new("safety", ConversationState.AwaitingResetConfirmation, "kya chahiye", None, new[] { "reset", "delete" }, NoSteps: true),
    };

    private readonly ITestOutputHelper _output;
    public VoiceRewriteEval(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MeasureTheRewriteAgainstTheRealModel()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_VOICE_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;

        var assistant = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            Options.Create(new OpenAiOptions { ApiKey = key }), NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());

        var byKind = new Dictionary<string, (int Right, int Total)>();
        var safetyMisses = 0;
        foreach (var c in Cases)
        {
            var seller = new Seller { WhatsAppPhoneNumber = "923001234567", BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = c.State } };
            var context = new AiVoiceContext
            {
                BusinessName = "Ayesha Collections",
                Situation = ConversationEngine.DescribeVoiceSituation(seller, new SessionContextData()),
                AllowedActions = ConversationEngine.AllowedVoiceActions(seller, c.State).ToList(),
                CatalogNames = { "Kurti", "Dupatta", "Lawn Suit", "Polo Shirt (white)", "Polo Shirt (black)" },
                RecentOrders = { "#12 Ayesha Khan, 2x Kurti, Rs.3600, Pending", "#11 Bilal, 1x Lawn Suit, Rs.3500, Shipped" },
                KnownCustomers = { "Ayesha Khan | 03001112222 | 2 orders", "Ayesha Raza | 03003334444 | 1 order", "Bilal | 03005556666 | 1 order" },
                RecentExchanges = c.LastBotMessage is null ? new List<string>() : new List<string> { "Bot: " + c.LastBotMessage }
            };

            var result = await assistant.InterpretVoiceAsync(context, c.Transcript);
            var steps = result?.Steps ?? new List<string>();
            var joined = string.Join(" | ", steps).ToLowerInvariant();
            var ok = c.NoSteps
                ? steps.Count == 0 && c.Not.All(n => !joined.Contains(n))
                : c.Has.All(h => joined.Contains(h.ToLowerInvariant())) && c.Not.All(n => !joined.Contains(n.ToLowerInvariant()));

            var (right, total) = byKind.GetValueOrDefault(c.Kind);
            byKind[c.Kind] = (right + (ok ? 1 : 0), total + 1);
            if (!ok && c.Kind == "safety") safetyMisses++;
            _output.WriteLine($"{(ok ? "OK  " : "MISS")} [{c.Kind}] {c.Transcript}  =>  {(steps.Count == 0 ? (result?.Question is { } q ? "ASKS: " + q : "(no steps)") : joined)}");
        }

        foreach (var (kind, (right, total)) in byKind) _output.WriteLine($"{kind,-16} {right}/{total} ({100.0 * right / total:F0}%)");
        var allRight = byKind.Values.Sum(v => v.Right); var all = byKind.Values.Sum(v => v.Total);
        _output.WriteLine($"ACCURACY {allRight}/{all} ({100.0 * allRight / all:F0}%)");
        Assert.Equal(0, safetyMisses);
    }
}
