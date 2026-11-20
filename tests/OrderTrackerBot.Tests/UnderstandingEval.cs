using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;
using Case = OrderTrackerBot.Tests.UnderstandingFixture.Case;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Phase 0 of docs/mvp-model-first-plan.md: scores the OLD understanding pipeline on the labelled MVP messages in
/// Fixtures/understanding-cases.json. The pipeline is exact rules first (CommandParser), then the AI order reader.
/// Rules-only numbers are always printed. The AI part runs only when OPENAI_API_KEY is set (OTB_UNDERSTANDING_EVAL=1 also required).
/// The test never fails on a score; it is the baseline. The production path (model-first) is scored by <see cref="TurnFixtureEval"/>.
/// </summary>
public class UnderstandingEval
{
    private readonly ITestOutputHelper _output;
    public UnderstandingEval(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MeasureTodaysPipelineOnLabelledMessages()
    {
        // Payment is dropped from the MVP jobs (user decision, 2026-11-10): its rows stay in the fixture but are not scored here.
        var all = UnderstandingFixture.Load();
        var cases = all.Where(c => c.Job != "payment").ToList();
        _output.WriteLine($"Scored {cases.Count} rows; excluded {all.Count - cases.Count} payment rows (out of MVP scope).");
        Assert.NotEmpty(cases);
        Assert.All(cases, c => Assert.Contains(c.Action, UnderstandingFixture.Labels));

        // Pass 1: exact rules only (no key needed; runs in CI).
        var ruleHits = 0;
        var unresolved = new List<Case>();
        var perJob = new Dictionary<string, (int Right, int Total)>();
        foreach (var c in cases)
        {
            var action = UnderstandingFixture.RuleAction(c.Text);
            var ok = ActionOk(c.Action, action);
            if (ok) ruleHits++;
            else if (action is null) unresolved.Add(c);
            Add(perJob, c.Job, ok);
        }
        _output.WriteLine($"RULES  right {ruleHits}/{cases.Count} ({Pct(ruleHits, cases.Count)})   not placed by rules: {unresolved.Count}");
        foreach (var (job, v) in perJob) _output.WriteLine($"  {job,-8} {v.Right}/{v.Total}");

        // Pass 2: the AI order reader for rows the rules did not place (only with a key).
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_UNDERSTANDING_EVAL") != "1" || string.IsNullOrWhiteSpace(key))
        {
            _output.WriteLine("AI pass skipped (set OTB_UNDERSTANDING_EVAL=1 and OPENAI_API_KEY to run it).");
            return;
        }

        var assistant = new OpenAiOrderAssistant(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            Options.Create(new OpenAiOptions { ApiKey = key }), NullLogger<OpenAiOrderAssistant>.Instance, Mock.Of<IIssueReporter>());
        var context = new AiAnalysisContext
        {
            BusinessName = "Ayesha Collections",
            // OTB_AI_CANCEL_UNDO=1 measures the reader with Features:AiCancelUndo on (cancel/undo intents and number words).
            CancelUndoIntents = Environment.GetEnvironmentVariable("OTB_AI_CANCEL_UNDO") == "1",
            Catalog = TurnFixtureEval.Catalog.ToList(),
        };

        var finalRight = ruleHits;
        var aiRight = 0;
        foreach (var c in unresolved)
        {
            var analysis = await assistant.AnalyzeMessageAsync(context, c.Text);
            var action = AiAction(analysis);
            var ok = ActionOk(c.Action, action) && FieldsOk(c, action, analysis);
            if (ok) { aiRight++; finalRight++; }
            var note = analysis.AiUnavailable ? " [AI fallback: call failed or unusable]" : "";
            _output.WriteLine($"{(ok ? "OK  " : "MISS")} {c.Id} [{c.Job}] expected {c.Action}, got {action ?? "none"}{note} | {c.Text}");
        }
        _output.WriteLine($"AI on unplaced rows: right {aiRight}/{unresolved.Count}");
        _output.WriteLine($"PIPELINE (rules then AI) right {finalRight}/{cases.Count} ({Pct(finalRight, cases.Count)})");
    }

    /// <summary>The MVP action the AI order reader's intent maps to.</summary>
    private static string? AiAction(AiMessageAnalysis analysis) => analysis.Intent switch
    {
        "new_order" => "create_order",
        "add_products" => "add_product",
        "status_update" => "update_status",
        "cancel_order" => "cancel_order",
        "undo" => "undo",
        "off_topic" or "unclear" => "help_or_chat",
        _ => null,
    };

    /// <summary>out_of_scope accepts help_or_chat too: the bot declining a cut feature is the MVP behaviour.</summary>
    private static bool ActionOk(string expected, string? got) =>
        got == expected || (expected == "out_of_scope" && got == "help_or_chat");

    /// <summary>Field check only where the analysis carries fields (orders and products).</summary>
    private static bool FieldsOk(Case c, string? action, AiMessageAnalysis analysis) =>
        c.MustContain is null || action is not ("create_order" or "add_product") || UnderstandingFixture.Carries(JsonSerializer.Serialize(analysis), c.MustContain);

    private static void Add(Dictionary<string, (int Right, int Total)> map, string job, bool ok)
    {
        var (right, total) = map.GetValueOrDefault(job);
        map[job] = (right + (ok ? 1 : 0), total + 1);
    }

    private static string Pct(int right, int total) => $"{100.0 * right / Math.Max(1, total):F0}%";
}
