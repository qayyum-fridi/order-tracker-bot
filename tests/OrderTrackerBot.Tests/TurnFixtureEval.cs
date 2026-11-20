using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;
using Case = OrderTrackerBot.Tests.UnderstandingFixture.Case;

namespace OrderTrackerBot.Tests;

/// <summary>
/// The 300-message check of docs/mvp-model-first-plan.md (Phase 1: at least 90% right action, under 2% wrong number) run on the PRODUCTION
/// path: an exact typed command is answered by CommandParser (the model never sees it), everything else by the model-first interpreter
/// (<see cref="OpenAiTurnInterpreter"/>), as in ConversationEngine.TryModelFirstAsync.
/// Live part: <c>OTB_TURN_EVAL=1 OPENAI_API_KEY=... dotnet test --filter TurnFixtureEval --logger "console;verbosity=detailed"</c>
/// (<c>OTB_TURN_EVAL_MODEL</c> picks another model). The offline tests check the fixture and the parser's share without a key.
/// Limits: the messages come from our own test docs, not sellers; voice rows are typed transcripts (speech-to-text is not measured);
/// one Urdu-script row. A pass here is necessary, not sufficient.
/// </summary>
public class TurnFixtureEval
{
    private const double PassRate = 0.90, MaxWrongNumberRate = 0.02;
    private readonly ITestOutputHelper _output;
    public TurnFixtureEval(ITestOutputHelper output) => _output = output;

    internal static readonly AiCatalogItem[] Catalog =
    {
        new() { Name = "Shirt", Price = 1800 }, new() { Name = "Dupatta", Price = 600 }, new() { Name = "Lawn Suit", Price = 3500 },
        new() { Name = "Sharara Set", Price = 4200 }, new() { Name = "Shawl", Price = 2500 }, new() { Name = "Polo Shirt", Price = 500 },
    };

    private static readonly string[] Customers =
    {
        "Sara Khan (03004445566, Model Town Lahore)", "Bilal Ahmed (03215557788, Saddar Karachi)", "Hina Raza (03331112233, DHA Lahore)",
    };

    private static readonly string[] RecentOrders = { "#12 Sara Khan Pending", "#3 Bilal Ahmed Shipped", "#2 Hina Raza Pending", "#1 Sara Khan Delivered" };

    /// <summary>
    /// Answers besides the label that are the right MVP behaviour. Kept short on purpose: each one widens what counts as right.
    /// out_of_scope: declining a cut feature as chat is the same reply. show_order: a named customer's latest order may come as their detail.
    /// record_payment: there is no payment action in the model-first list, so an honest "not yet" is the MVP answer.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> AlsoRight = new Dictionary<string, string[]>
    {
        ["out_of_scope"] = new[] { TurnAction.HelpOrChat },
        ["help_or_chat"] = new[] { TurnAction.HowTo },
        ["show_order"] = new[] { TurnAction.ShowCustomer },
        ["record_payment"] = new[] { TurnAction.OutOfScope },
        // A price change for a product the catalog has: the label predates edit_product.
        ["add_product"] = new[] { TurnAction.EditProduct },
    };

    /// <summary>
    /// Rows the exact parser answers wrongly today. In production the model never sees them, so no prompt change fixes them; they count as
    /// wrong in the live score. A new miss, or a miss that got fixed, fails <see cref="Exact_commands_the_parser_answers_are_the_labelled_action"/>
    /// so this list stays true.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> KnownParserMisses = new Dictionary<string, string>
    {
        ["U070"] = "'Shirt 3000, Lawn Suit 4000' is read as adding products (AddProductsBulk), not an order",
        ["U071"] = "'Shirt 1800, Dupatta 600, Lawn Suit 3500' is read as adding products (AddProductsBulk), not an order",
        ["U089"] = "walk-in sale: cut by Features:MvpOnly, the seller gets 'not available yet'",
        ["U090"] = "counter sale: cut by Features:MvpOnly",
        ["U091"] = "'dukan par ...' sale: cut by Features:MvpOnly",
        ["U132"] = "'order 2 undo' gets the notice that undo reverts only the last action",
        ["U299"] = "'weekly summary email karo' shows the summary and ignores the email request",
    };

    internal static bool ActionOk(string expected, string? got) =>
        got == expected || (got is not null && AlsoRight.TryGetValue(expected, out var also) && also.Contains(got));

    private enum Outcome { Right, WrongNumber, Asked, Wrong, NoAnswer }

    private sealed record Result(Case Case, string Route, string? Got, Outcome Outcome, long Ms, string? Detail);

    [Fact]
    public async Task Production_path_on_the_300_labelled_messages()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;
        var options = new OpenAiOptions { ApiKey = key };
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL_MODEL") is { Length: > 0 } model) options.Model = model;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var interpreter = new OpenAiTurnInterpreter(http, Options.Create(options), NullLogger<OpenAiTurnInterpreter>.Instance);

        var gate = new SemaphoreSlim(6);
        var results = await Task.WhenAll(UnderstandingFixture.Load().Select(async c =>
        {
            if (UnderstandingFixture.ExactCommandAction(c.Text) is { } byParser)
                return new Result(c, "parser", byParser, ActionOk(c.Action, byParser) ? Outcome.Right : Outcome.Wrong, 0, null);

            await gate.WaitAsync();
            try
            {
                var clock = Stopwatch.StartNew();
                var context = new TurnContext
                {
                    BusinessName = "Ayesha Collections", ShopType = "Clothing", Catalog = Catalog, CatalogSize = Catalog.Length,
                    KnownCustomers = Customers, RecentOrders = RecentOrders, FromVoice = c.Voice,
                };
                var action = await interpreter.UnderstandAsync(context, c.Text);
                var ms = clock.ElapsedMilliseconds;
                if (action is null) return new Result(c, "model", null, Outcome.NoAnswer, ms, null);
                var json = JsonSerializer.Serialize(action);
                var outcome = !ActionOk(c.Action, action.Action)
                    ? action.Action == TurnAction.Clarify ? Outcome.Asked : Outcome.Wrong
                    : c.MustContain is not null && !UnderstandingFixture.Carries(json, c.MustContain) ? Outcome.WrongNumber
                    : Outcome.Right;
                return new Result(c, "model", action.Action, outcome, ms, outcome == Outcome.Right ? null : json);
            }
            finally { gate.Release(); }
        }));

        // OpenAiTurnInterpreter returns null on an HTTP error, so a bad key would otherwise read as a low score.
        var modelRows = results.Count(r => r.Route == "model");
        Assert.False(modelRows > 0 && results.Count(r => r.Outcome == Outcome.NoAnswer) == modelRows,
            $"All {modelRows} model calls came back empty: the key, model name or network failed. No score was measured.");

        Report(options.Model, results);

        var right = results.Count(r => r.Outcome == Outcome.Right);
        var withField = results.Count(r => r.Case.MustContain is not null);
        var wrongNumber = results.Count(r => r.Outcome == Outcome.WrongNumber);
        Assert.True(right >= results.Length * PassRate, $"{right}/{results.Length} right, below {PassRate:P0}");
        Assert.True(wrongNumber <= withField * MaxWrongNumberRate, $"{wrongNumber}/{withField} rows lost a number or name, above {MaxWrongNumberRate:P0}");
    }

    private void Report(string model, Result[] results)
    {
        foreach (var r in results.Where(r => r.Outcome != Outcome.Right).OrderBy(r => r.Outcome).ThenBy(r => r.Case.Id))
            _output.WriteLine($"{r.Outcome,-11} {r.Case.Id} [{r.Case.Job}/{r.Route}] wanted {r.Case.Action}{(r.Case.MustContain is { } m ? $" with \"{m}\"" : "")}, got {r.Got ?? "nothing"} | {r.Case.Text}" +
                              (r.Detail is null ? "" : $"\n            {r.Detail}"));

        var n = results.Length;
        var right = results.Count(r => r.Outcome == Outcome.Right);
        var (low, high) = Wilson(right, n);
        _output.WriteLine($"\n{model}: right {right}/{n} ({Pct(right, n)}, 95% interval {low:P0}-{high:P0})");
        foreach (var outcome in Enum.GetValues<Outcome>().Where(o => o != Outcome.Right))
            _output.WriteLine($"  {outcome,-11} {results.Count(r => r.Outcome == outcome)}");
        var withField = results.Count(r => r.Case.MustContain is not null);
        _output.WriteLine($"  wrong number/name: {results.Count(r => r.Outcome == Outcome.WrongNumber)}/{withField} rows that carry one");

        Group("route", results, r => r.Route);
        Group("job", results, r => r.Case.Job);
        Group("channel", results, r => r.Case.Voice ? "voice" : "typed");
        Group("script", results, r => r.Case.UrduScript ? "urdu" : "latin");

        var times = results.Where(r => r.Route == "model").Select(r => r.Ms).OrderBy(ms => ms).ToList();
        if (times.Count > 0)
            _output.WriteLine($"\nmodel calls {times.Count}; latency median {times[times.Count / 2]} ms, p95 {times[(int)(times.Count * 0.95)]} ms, max {times[^1]} ms");
    }

    private void Group(string title, Result[] results, Func<Result, string> key)
    {
        _output.WriteLine($"by {title}:");
        foreach (var g in results.GroupBy(key).OrderBy(g => g.Key))
            _output.WriteLine($"  {g.Key,-10} {g.Count(r => r.Outcome == Outcome.Right)}/{g.Count()} ({Pct(g.Count(r => r.Outcome == Outcome.Right), g.Count())})");
    }

    /// <summary>Wilson score interval: honest bounds for a pass rate near 100% on a few hundred rows.</summary>
    internal static (double Low, double High) Wilson(int right, int n, double z = 1.96)
    {
        if (n == 0) return (0, 1);
        double p = (double)right / n, z2 = z * z;
        var centre = (p + z2 / (2 * n)) / (1 + z2 / n);
        var half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / (1 + z2 / n);
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    private static string Pct(int right, int total) => $"{100.0 * right / Math.Max(1, total):F0}%";

    // ----- Offline: no key needed -----

    [Fact]
    public void Fixture_has_300_unique_rows_with_known_labels()
    {
        var cases = UnderstandingFixture.Load();
        Assert.Equal(300, cases.Count);
        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());
        Assert.All(cases, c => Assert.Contains(c.Action, UnderstandingFixture.Labels));
        Assert.All(cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Text), c.Id));
        // Every label and accepted alternative is something the production path can answer.
        var answerable = TurnAction.All.Append("record_payment").ToHashSet();
        Assert.All(UnderstandingFixture.Labels, l => Assert.Contains(l, answerable));
        Assert.All(AlsoRight.Values.SelectMany(v => v), a => Assert.Contains(a, TurnAction.All));
    }

    [Fact]
    public void Exact_commands_the_parser_answers_are_the_labelled_action()
    {
        // These rows never reach the model in production, so a wrong parser reading is a wrong answer no prompt can fix.
        var cases = UnderstandingFixture.Load();
        var byParser = cases.Select(c => (Case: c, Got: UnderstandingFixture.ExactCommandAction(c.Text))).Where(x => x.Got is not null).ToList();
        var wrong = byParser.Where(x => !ActionOk(x.Case.Action, x.Got)).ToList();
        foreach (var x in wrong)
            _output.WriteLine($"PARSER WRONG {x.Case.Id}: wanted {x.Case.Action}, got {x.Got} ({CommandParser.TryParse(x.Case.Text)!.Kind}) | {x.Case.Text}");
        _output.WriteLine($"parser answers {byParser.Count}/{cases.Count} rows ({byParser.Count - wrong.Count} right); the model answers {cases.Count - byParser.Count}");
        Assert.Equal(KnownParserMisses.Keys.OrderBy(k => k), wrong.Select(x => x.Case.Id).OrderBy(k => k));
    }

    [Fact]
    public void Wilson_interval_is_sane()
    {
        var (low, high) = Wilson(270, 300);
        Assert.InRange(low, 0.85, 0.87);
        Assert.InRange(high, 0.92, 0.94);
        Assert.Equal(1.0, Wilson(300, 300).High, 6);
    }
}
