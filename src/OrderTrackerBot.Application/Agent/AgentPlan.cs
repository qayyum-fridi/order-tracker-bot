using System.Text.Json;

namespace OrderTrackerBot.Application.Agent;

/// <summary>One argument of a planner tool. <see cref="Kind"/> is "text", "number", "product_id" or "customer_id" (ids come from the seller context only).</summary>
public sealed record AgentToolArg(string Name, string Kind, bool Required);

/// <summary>An action the planner may put in a plan. The engine runs the real handler; the model only names the tool and its arguments.</summary>
public sealed record AgentTool(string Name, string Description, IReadOnlyList<AgentToolArg> Args, bool Risky = false, bool TakesItems = false);

/// <summary>An order line in a <c>draft_order</c> step: the product by id when the context has it, else by the name the seller said.</summary>
public sealed record PlanItem(int? ProductId, string Name, int Quantity);

public sealed record PlanStep(string Tool, IReadOnlyDictionary<string, string> Args, IReadOnlyList<PlanItem> Items);

/// <summary>What the seller promised to keep sending ("3 products, ek ek karke"): the tool the next messages are for and how many are still to come (0 = until done).</summary>
public sealed record PlanAwaiting(string Tool, int Count);

/// <summary>
/// The planner's answer for one message: ordered steps to run, or a question to ask first, or a promise of more input. Nothing here has run yet;
/// the verifier checks every step against the database before the engine executes it.
/// </summary>
public sealed record AgentPlan(IReadOnlyList<PlanStep> Steps, string? Question, PlanAwaiting? Awaiting, double Confidence);

/// <summary>The tools the planner may use. Adding a capability to the planner = adding an entry here and a handler that runs it.</summary>
public static class AgentTools
{
    public const int MaxSteps = 8;

    public static readonly IReadOnlyList<AgentTool> All = new List<AgentTool>
    {
        new("add_product", "Add one product to the catalog.",
            new[] { new AgentToolArg("name", "text", true), new AgentToolArg("price", "number", true), new AgentToolArg("cost", "number", false), new AgentToolArg("stock", "number", false) }),
        new("edit_product", "Change the price, cost or stock of a product that is already in the catalog.",
            new[] { new AgentToolArg("product_id", "product_id", true), new AgentToolArg("price", "number", false), new AgentToolArg("cost", "number", false), new AgentToolArg("stock", "number", false) },
            Risky: true),
        new("draft_order", "Start an order for a customer. It is only a draft: the seller still taps Save, Edit or Cancel.",
            new[] { new AgentToolArg("customer_id", "customer_id", false), new AgentToolArg("customer_name", "text", false), new AgentToolArg("phone", "text", false) },
            TakesItems: true),
    };

    public static AgentTool? Find(string name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
}

/// <summary>Reads the planner's JSON. Anything outside the tool list, or malformed, makes the whole plan unusable (null): a half-understood plan is never run.</summary>
public static class AgentPlanParser
{
    public static AgentPlan? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var steps = new List<PlanStep>();
            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                if (stepsEl.GetArrayLength() > AgentTools.MaxSteps) return null;
                foreach (var s in stepsEl.EnumerateArray())
                {
                    if (ParseStep(s) is not { } step) return null;
                    if (step.Args.Count == 0 && step.Items.Count == 0) continue;   // a step with nothing in it (every argument null) carries no task
                    // The model sometimes repeats a step; the same product action twice in one plan is one task. (Two orders are never merged.)
                    if (step.Tool != "draft_order" && steps.Any(x => x.Tool == step.Tool && x.Args.Count == step.Args.Count && !x.Args.Except(step.Args).Any())) continue;
                    steps.Add(step);
                }
            }

            var question = Text(root, "question");
            PlanAwaiting? awaiting = null;
            if (root.TryGetProperty("awaiting", out var aw) && aw.ValueKind == JsonValueKind.Object)
            {
                var tool = Text(aw, "tool");
                if (tool is null || AgentTools.Find(tool) is null) return null;
                var count = aw.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? Math.Clamp(n, 0, 100) : 0;
                awaiting = new PlanAwaiting(tool, count);
            }

            var confidence = root.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number ? Math.Clamp(conf.GetDouble(), 0, 1) : 0;
            if (steps.Count == 0 && question is null && awaiting is null) return null;
            return new AgentPlan(steps, question, awaiting, confidence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlanStep? ParseStep(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object || Text(s, "tool") is not { } name || AgentTools.Find(name) is not { } tool) return null;

        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (s.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject())
            {
                var empty = p.Value.ValueKind == JsonValueKind.Null || (p.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(p.Value.GetString()));
                if (empty) continue;   // null / "" = not said, even for an argument this tool does not have
                if (!tool.Args.Any(x => x.Name == p.Name)) return null;   // a value for an argument the tool does not have
                if (p.Value.ValueKind == JsonValueKind.String) { if (!string.IsNullOrWhiteSpace(p.Value.GetString())) args[p.Name] = p.Value.GetString()!.Trim(); }   // "" = not said
                else if (p.Value.ValueKind == JsonValueKind.Number) args[p.Name] = p.Value.GetRawText();
                else if (p.Value.ValueKind != JsonValueKind.Null) return null;
            }

        var items = new List<PlanItem>();
        if (s.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array && its.GetArrayLength() > 0)
        {
            if (!tool.TakesItems) return null;   // an empty list is harmless on any tool; a filled one on a tool without items is not
            foreach (var i in its.EnumerateArray())
            {
                if (i.ValueKind != JsonValueKind.Object) return null;
                var itemName = Text(i, "name") ?? "";
                int? productId = i.TryGetProperty("product_id", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var id) ? id : null;
                var qty = i.TryGetProperty("qty", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var n) ? n : 1;
                if ((productId is null && itemName.Length == 0) || qty is < 1 or > 999) return null;
                items.Add(new PlanItem(productId, itemName, qty));
            }
        }
        return new PlanStep(tool.Name, args, items);
    }

    private static string? Text(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
}
