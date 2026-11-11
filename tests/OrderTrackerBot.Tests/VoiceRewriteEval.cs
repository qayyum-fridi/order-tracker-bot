using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
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
        new("order", Idle, "Ayesha ke liye teen Shirt aur do Dupatta, delivery do sau", new[] { "ayesha", "3", "shirt", "2", "dupatta", "200" }, None),
        new("order", Idle, "ek naya order aaya hai Hamira ka, usne das lawn suit mangwaye, aath sau ka lagaya hai", new[] { "hamira", "10", "lawn suit", "800" }, None),
        new("order", Idle, "Sara ko ek Polo Shirt white chahiye, phone zero teen zero zero ek do teen chaar paanch chhe saat", new[] { "sara", "polo shirt", "03001234567" }, None),
        // commands
        new("command", Idle, "aaj kitna bika", new[] { "hisaab" }, None),
        new("command", Idle, "order paanch shipped kar do", new[] { "5", "shipped" }, None),
        new("command", Idle, "Shirt ka stock bees kar do", new[] { "stock", "shirt", "20" }, None),
        new("command", Idle, "catalog dikhao", new[] { "catalog" }, None),
        new("command", Idle, "kharcha petrol paanch sau", new[] { "500", "petrol" }, None),
        new("command", Idle, "Ali ko paanch hazar tankhwah di", new[] { "ali", "5000" }, None),
        new("command", Idle, "pichle hafte ka hisaab dikhao", new[] { "hafte" }, None),
        new("command", Idle, "kya karun", new[] { "kya karun" }, None),
        new("command", Idle, "order teen cancel kar do", new[] { "cancel", "3" }, None),
        // numbers said in words
        new("numbers", Idle, "Lawn Suit teen hazar do sau ka hai", new[] { "lawn suit", "3200" }, None),
        new("numbers", Idle, "Shirt ki price ek hazar paanch sau kar do", new[] { "shirt", "1500" }, None),
        // the seller corrects himself while speaking
        new("self-correction", Idle, "Ayesha ko do Shirt nahi paanch Shirt bhejni hain", new[] { "ayesha", "5" }, new[] { "2 shirt" }),
        new("self-correction", Idle, "Shirt ki price do hazar nahi ek hazar paanch sau kar do", new[] { "1500" }, new[] { "2000" }),
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

        // Two pipelines on the same sentences: the full rewrite alone (Features:VoiceIntentFirst off) and intent-first (on).
        var rewriteByKind = new Dictionary<string, (int Right, int Total)>();
        var firstByKind = new Dictionary<string, (int Right, int Total)>();
        var safetyMissesIntentFirst = 0;
        foreach (var c in Cases)
        {
            var seller = new Seller { WhatsAppPhoneNumber = "923001234567", BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = c.State } };
            var context = new AiVoiceContext
            {
                BusinessName = "Ayesha Collections",
                Situation = ConversationEngine.DescribeVoiceSituation(seller, new SessionContextData()),
                AllowedActions = ConversationEngine.AllowedVoiceActions(seller, c.State).ToList(),
                CatalogNames = { "Shirt", "Dupatta", "Lawn Suit", "Polo Shirt (white)", "Polo Shirt (black)" },
                RecentOrders = { "#12 Ayesha Khan, 2x Shirt, Rs.3600, Pending", "#11 Bilal, 1x Lawn Suit, Rs.3500, Shipped" },
                KnownCustomers = { "Ayesha Khan | 03001112222 | 2 orders", "Ayesha Raza | 03003334444 | 1 order", "Bilal | 03005556666 | 1 order" },
                RecentExchanges = c.LastBotMessage is null ? new List<string>() : new List<string> { "Bot: " + c.LastBotMessage }
            };

            var result = await assistant.InterpretVoiceAsync(context, c.Transcript);
            var rewriteSteps = result?.Steps ?? new List<string>();
            var rewriteOk = Passes(c, rewriteSteps);
            Add(rewriteByKind, c.Kind, rewriteOk);

            // Intent-first, as the engine does it for an idle seller: chat -> nothing runs; command -> the intent catalog, behind the amount guard; else the rewrite.
            var firstSteps = rewriteSteps;
            var path = "rewrite";
            if (c.State == ConversationState.Idle)
            {
                var kind = await assistant.ClassifyVoiceKindAsync(c.Transcript) ?? "unavailable";
                path = kind;
                if (kind == "chat") firstSteps = new List<string>();
                else if (kind == "command")
                {
                    var route = await assistant.RouteIntentAsync(c.Transcript, IntentCatalog.All);
                    if (route is { Confidence: >= 0.7 } && IntentCatalog.Find(route.Intent) is { } intent
                        && IntentCatalog.Build(intent, route.Slots).Command is { } command
                        && ConversationEngine.WhyUnfaithful(c.Transcript, command, null) is null)
                        firstSteps = new List<string> { command };
                    else path = "command, router did not place it: rewrite";
                }
            }
            var firstOk = Passes(c, firstSteps);
            Add(firstByKind, c.Kind, firstOk);
            if (!firstOk && c.Kind == "safety") safetyMissesIntentFirst++;

            _output.WriteLine($"rewrite {(rewriteOk ? "OK  " : "MISS")} | intent-first {(firstOk ? "OK  " : "MISS")} [{c.Kind}] {c.Transcript}");
            _output.WriteLine($"          rewrite: {Show(rewriteSteps, result?.Question)}   |   intent-first ({path}): {Show(firstSteps, null)}");
        }

        _output.WriteLine("kind              rewrite     intent-first");
        foreach (var kind in rewriteByKind.Keys)
            _output.WriteLine($"{kind,-16}  {Pct(rewriteByKind[kind]),-10}  {Pct(firstByKind[kind])}");
        _output.WriteLine($"ACCURACY rewrite {Pct(Sum(rewriteByKind))}   intent-first {Pct(Sum(firstByKind))}");
        Assert.Equal(0, safetyMissesIntentFirst);
    }

    private static bool Passes(Case c, List<string> steps)
    {
        var joined = string.Join(" | ", steps).ToLowerInvariant();
        return c.NoSteps
            ? steps.Count == 0 && c.Not.All(n => !joined.Contains(n.ToLowerInvariant()))
            : c.Has.All(h => joined.Contains(h.ToLowerInvariant())) && c.Not.All(n => !joined.Contains(n.ToLowerInvariant()));
    }

    private static string Show(List<string> steps, string? question) =>
        steps.Count > 0 ? string.Join(" | ", steps) : question is not null ? "ASKS: " + question : "(no steps)";

    private static void Add(Dictionary<string, (int Right, int Total)> map, string kind, bool ok)
    {
        var (right, total) = map.GetValueOrDefault(kind);
        map[kind] = (right + (ok ? 1 : 0), total + 1);
    }

    private static (int Right, int Total) Sum(Dictionary<string, (int Right, int Total)> map) => (map.Values.Sum(v => v.Right), map.Values.Sum(v => v.Total));
    private static string Pct((int Right, int Total) v) => $"{v.Right}/{v.Total} ({100.0 * v.Right / Math.Max(1, v.Total):F0}%)";
}
