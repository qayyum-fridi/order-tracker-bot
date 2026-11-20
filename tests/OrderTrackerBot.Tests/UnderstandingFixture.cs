using System.Text.Json;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Conversation;

namespace OrderTrackerBot.Tests;

/// <summary>
/// The 300 labelled MVP messages in Fixtures/understanding-cases.json (written from the test docs, not real seller messages),
/// shared by <see cref="UnderstandingEval"/> (old pipeline) and <see cref="TurnFixtureEval"/> (the production path).
/// </summary>
internal static class UnderstandingFixture
{
    internal sealed record Case(string Id, string Job, string Text, string Action, string? MustContain, string Source)
    {
        /// <summary>A voice transcript (rows taken from the voice evals), not a typed message.</summary>
        public bool Voice => Source.Contains("voice", StringComparison.OrdinalIgnoreCase);

        public bool UrduScript => UrduLetters.IsMatch(Text);
    }

    private static readonly Regex UrduLetters = new(@"[؀-ۿ]");

    /// <summary>Every label the fixture may use. record_payment has no model-first action: payment was cut from the MVP (2026-11-10).</summary>
    internal static readonly HashSet<string> Labels = new()
    {
        "create_order", "update_status", "cancel_order", "record_payment", "show_orders", "show_order", "show_summary",
        "add_product", "show_catalog", "undo", "help_or_chat", "out_of_scope", "show_customer"
    };

    internal static List<Case> Load()
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

    /// <summary>The MVP action a parsed command amounts to.</summary>
    internal static string RuleAction(ParsedCommand parsed) => parsed.Kind switch
    {
        CommandKind.OrderPage or CommandKind.OrderDetail => "show_order",
        CommandKind.CustomerDetail => "show_customer",
        CommandKind.OrdersToday or CommandKind.PendingOrders or CommandKind.UnpaidOrders or CommandKind.CodPending => "show_orders",
        CommandKind.TodaysSummary or CommandKind.WeeklySummary or CommandKind.TrendingProducts => "show_summary",
        // "mark 12 paid" is how a typed payment is recorded.
        CommandKind.MarkStatus when parsed.Text == "paid" => "record_payment",
        // MarkAllTodayDelivered is left out on purpose: it changes every order of today, and the only row that parses to it (U124) asks for
        // one customer's orders, so scoring it as a status update would hide a wrong bulk change.
        CommandKind.MarkStatus or CommandKind.MarkAllPendingShipped => "update_status",
        CommandKind.CancelOrder => "cancel_order",
        CommandKind.OrderPayment => "record_payment",
        CommandKind.Catalog => "show_catalog",
        CommandKind.AddProduct or CommandKind.AddProductsBulk => "add_product",
        CommandKind.EditProduct => "edit_product",
        CommandKind.Undo or CommandKind.UndoNote => "undo",
        CommandKind.Help or CommandKind.Menu or CommandKind.Greeting or CommandKind.Guide or CommandKind.Start or CommandKind.NewOrderHelp
            or CommandKind.Notice => "help_or_chat",
        // Any other command the rules place is a feature outside the MVP jobs; answering it counts as out_of_scope.
        _ => "out_of_scope",
    };

    /// <summary>The action the exact rules give for a message, or null when the rules do not place it.</summary>
    internal static string? RuleAction(string text) => CommandParser.TryParse(text) is { } parsed ? RuleAction(parsed) : null;

    /// <summary>
    /// The production split (ConversationEngine.TryModelFirstAsync): an exact typed command keeps its fast path and never reaches the model;
    /// a loose keyword rule or no command at all goes to the model. Returns the parser's action, or null when the model answers.
    /// </summary>
    internal static string? ExactCommandAction(string text) =>
        CommandParser.TryParse(text) is { } parsed && !ConversationEngine.LooseRuleKinds.Contains(parsed.Kind) ? RuleAction(parsed) : null;

    /// <summary>A field the answer must carry (a phone, a price, a name). Phones match with or without the +92 / 92 form, spaces and dashes ignored.</summary>
    internal static bool Carries(string serialised, string mustContain)
    {
        var json = serialised.ToLowerInvariant();
        var compact = json.Replace(" ", "").Replace("-", "");
        var want = mustContain.ToLowerInvariant();
        if (compact.Contains(want.Replace(" ", "").Replace("-", "")) || json.Contains(want)) return true;
        return want.StartsWith('0') && want.Length == 11 && (compact.Contains("+92" + want[1..]) || compact.Contains("92" + want[1..]));
    }
}
