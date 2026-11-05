using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Measures the text ROUTER (message -> catalog intent) against the REAL model. It prints every answer with its confidence and a summary per kind.
/// It is a measurement, not a gate: it never fails on a wrong answer. Skipped unless OTB_ROUTER_EVAL=1 and OPENAI_API_KEY are set:
/// <c>OTB_ROUTER_EVAL=1 OPENAI_API_KEY=... dotnet test --filter RouterEval --logger "console;verbosity=detailed"</c>
/// A case passes when the router names <see cref="Expected"/> with at least the route confidence (0.7). "none" means nothing should run.
/// </summary>
public class RouterEval
{
    private sealed record Case(string Kind, string Message, string Expected);

    private const string Guide = "account.guide";
    private const string Support = "support.contact";
    private const string CustomerSearch = "customer.search";
    private const string CustomerList = "customer.list";
    private const string EditLatest = "order.edit_latest";
    private const string StaffAdd = "staff.add";

    private static readonly Case[] Cases =
    {
        // what this app is for
        new("guide", "Mera kis kam ki yah application ha", Guide),
        new("guide", "yeh bot kya karta hai", Guide),
        new("guide", "what is this app for", Guide),
        new("guide", "یہ ایپ کس کام کی ہے", Guide),
        // how to do something
        new("how-to", "order ko edit kaise karun", EditLatest),
        new("how-to", "staff member kaise add karte hain", StaffAdd),
        // customer search, with typos and mixed scripts
        new("search", "Ali koi customers ha mera pass تلاش karo", CustomerSearch),
        new("search", "Ali naam ka customer dhoondo", CustomerSearch),
        new("search", "Sara naam ka customer talash karo", CustomerSearch),
        new("list", "customers dikhao", CustomerList),
        // support, with the messages that failed live
        new("support", "mujhe support se baat karni hai", Support),
        new("support", "Customers support ka document do", Support),
        new("support", "kisi insaan se baat karao", Support),
        // nothing should run
        new("chat", "assalam o alaikum kya haal hai", "none"),
        new("chat", "aaj mausam bohat garam hai", "none"),
    };

    private readonly ITestOutputHelper _output;
    public RouterEval(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MeasureTheRouterAgainstTheRealModel()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_ROUTER_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;

        var assistant = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            Options.Create(new OpenAiOptions { ApiKey = key }), NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());

        var summary = new Dictionary<string, (int Right, int Total)>();
        foreach (var c in Cases)
        {
            var route = await assistant.RouteIntentAsync(c.Message, IntentCatalog.All);
            var named = route?.Intent ?? "(no answer)";
            var confidence = route?.Confidence ?? 0;
            var ok = c.Expected == "none"
                ? route is null || confidence < 0.7
                : route is not null && route.Intent == c.Expected && confidence >= 0.7;

            _output.WriteLine($"{(ok ? "PASS" : "MISS")}  [{c.Kind}] {c.Message}");
            _output.WriteLine($"      expected {c.Expected}  got {named}  confidence {confidence:0.00}");

            var (right, total) = summary.GetValueOrDefault(c.Kind);
            summary[c.Kind] = (right + (ok ? 1 : 0), total + 1);
        }

        _output.WriteLine("");
        foreach (var (kind, (right, total)) in summary)
            _output.WriteLine($"{kind,-10} {right}/{total}");
        _output.WriteLine($"TOTAL      {summary.Values.Sum(v => v.Right)}/{summary.Values.Sum(v => v.Total)}");
    }
}
