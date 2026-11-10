using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Phase 0 of docs/mvp-model-first-plan.md: scores today's understanding pipeline on the labelled MVP messages in
/// Fixtures/understanding-cases.json. The pipeline mirrors the engine's order: exact rules first (CommandParser), then the AI order reader.
/// Rules-only numbers are always printed. The AI part runs only when OPENAI_API_KEY is set (OTB_UNDERSTANDING_EVAL=1 also required).
/// The test never fails on a score; it is the baseline that later phases must beat.
/// </summary>
public class UnderstandingEval
{
    private sealed record Case(string Id, string Job, string Text, string Action, string? MustContain, string Source);

    private static readonly HashSet<string> MvpActions = new()
    {
        "create_order", "update_status", "cancel_order", "record_payment", "show_orders", "show_order", "show_summary",
        "add_product", "show_catalog", "undo", "help_or_chat", "out_of_scope"
    };

    private readonly ITestOutputHelper _output;
    public UnderstandingEval(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MeasureTodaysPipelineOnLabelledMessages()
    {
        var cases = LoadCases();
        Assert.NotEmpty(cases);
        Assert.All(cases, c => Assert.Contains(c.Action, MvpActions));

        // Pass 1: exact rules only (no key needed; runs in CI).
        var ruleHits = 0;
        var unresolved = new List<Case>();
        var perJob = new Dictionary<string, (int Right, int Total)>();
        foreach (var c in cases)
        {
            var action = RuleAction(c.Text);
            var ok = action == c.Action;
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
            Catalog = new List<AiCatalogItem>
            {
                new() { Name = "Kurti", Price = 1800 }, new() { Name = "Dupatta", Price = 600 }, new() { Name = "Lawn Suit", Price = 3500 },
                new() { Name = "Sharara Set", Price = 4200 }, new() { Name = "Shawl", Price = 2500 }, new() { Name = "Polo Shirt", Price = 500 },
            },
        };

        var finalRight = ruleHits;
        var aiRight = 0;
        foreach (var c in unresolved)
        {
            var analysis = await assistant.AnalyzeMessageAsync(context, c.Text);
            var action = AiAction(analysis);
            var serialized = JsonSerializer.Serialize(analysis).ToLowerInvariant();
            var ok = action == c.Action && (c.MustContain is null || serialized.Contains(c.MustContain.ToLowerInvariant()));
            if (ok) { aiRight++; finalRight++; }
            _output.WriteLine($"{(ok ? "OK  " : "MISS")} {c.Id} [{c.Job}] expected {c.Action}, got {action ?? "none"} | {c.Text}");
        }
        _output.WriteLine($"AI on unplaced rows: right {aiRight}/{unresolved.Count}");
        _output.WriteLine($"PIPELINE (rules then AI) right {finalRight}/{cases.Count} ({Pct(finalRight, cases.Count)})");
    }

    /// <summary>The action the exact rules give for a message, or null when the rules do not place it.</summary>
    private static string? RuleAction(string text)
    {
        var parsed = CommandParser.TryParse(text);
        if (parsed is null) return null;
        return parsed.Kind switch
        {
            CommandKind.OrderPage or CommandKind.OrderDetail or CommandKind.CustomerDetail => "show_order",
            CommandKind.OrdersToday or CommandKind.PendingOrders or CommandKind.UnpaidOrders or CommandKind.CodPending => "show_orders",
            CommandKind.TodaysSummary => "show_summary",
            CommandKind.MarkStatus or CommandKind.MarkAllPendingShipped => "update_status",
            CommandKind.CancelOrder => "cancel_order",
            CommandKind.OrderPayment => "record_payment",
            CommandKind.Catalog => "show_catalog",
            CommandKind.AddProduct or CommandKind.AddProductsBulk => "add_product",
            CommandKind.Undo => "undo",
            CommandKind.Help or CommandKind.Menu or CommandKind.Greeting => "help_or_chat",
            // Any other command the rules place is a feature the MVP cuts; answering it counts as out_of_scope.
            _ => "out_of_scope",
        };
    }

    /// <summary>The MVP action the AI order reader's intent maps to.</summary>
    private static string? AiAction(AiMessageAnalysis analysis) => analysis.Intent switch
    {
        "new_order" => "create_order",
        "add_products" => "add_product",
        "status_update" => "update_status",
        "off_topic" or "unclear" => "help_or_chat",
        _ => null,
    };

    private static List<Case> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "understanding-cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(e => new Case(
            e.GetProperty("id").GetString()!,
            e.GetProperty("job").GetString()!,
            e.GetProperty("text").GetString()!,
            e.GetProperty("action").GetString()!,
            e.TryGetProperty("mustContain", out var m) ? m.GetString() : null,
            e.GetProperty("source").GetString()!)).ToList();
    }

    private static void Add(Dictionary<string, (int Right, int Total)> map, string job, bool ok)
    {
        var (right, total) = map.GetValueOrDefault(job);
        map[job] = (right + (ok ? 1 : 0), total + 1);
    }

    private static string Pct(int right, int total) => $"{100.0 * right / Math.Max(1, total):F0}%";
}
