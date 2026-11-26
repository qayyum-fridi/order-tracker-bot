using System.Globalization;
using OrderTrackerBot.Application.Ai;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Stage 3 evidence and scoring. Five features, each 0 to 1 or null (not applicable). Intent confidence, pattern, entity alignment,
/// context and argument evidence are separate: a candidate can be sure of its intent and still lack arguments (Stage 5b asks for those).
/// The composite is the weighted mean of the features present, so a missing feature neither helps nor hurts.
/// </summary>
public static class CandidateScorer
{
    public const double IntentWeight = 0.35;
    public const double PatternWeight = 0.20;
    public const double EntityWeight = 0.20;
    public const double ContextWeight = 0.15;
    public const double ArgumentWeight = 0.10;

    /// <summary>A deterministic partial match (intent words found, a required part missing) supports its intent only this far.</summary>
    public const double PartialPattern = 0.5;

    public static double? Composite(double? intent, double? pattern, double? entity, double? context, double? argument)
    {
        double sum = 0, weight = 0;
        Add(intent, IntentWeight);
        Add(pattern, PatternWeight);
        Add(entity, EntityWeight);
        Add(context, ContextWeight);
        Add(argument, ArgumentWeight);
        return weight == 0 ? null : sum / weight;

        void Add(double? feature, double w)
        {
            if (feature is not { } v) return;
            sum += w * v;
            weight += w;
        }
    }

    /// <summary>Entity: the order number the action names agrees with the message (1), contradicts it (0), or the message names none (null).</summary>
    internal static double? EntityAlignment(TurnAction action, IReadOnlyList<ConversationEntity> entities)
    {
        if (action.OrderId is not { } id) return null;
        var numbers = entities.Where(e => e.Kind == "order_id").Select(e => int.TryParse(e.Value, out var n) ? n : -1).ToList();
        if (numbers.Count == 0) return null;
        return numbers.Contains(id) ? 1 : 0;
    }

    /// <summary>
    /// Argument evidence: of the identifying arguments the action supplies, the share the message actually contains.
    /// Free-text values are left out on purpose: a model often fills them from context, so their absence says little.
    /// </summary>
    internal static double? ArgumentEvidence(TurnAction action, IReadOnlyList<ConversationEntity> entities, string input)
    {
        var supplied = new List<bool>();
        if (action.OrderId is { } id)
            supplied.Add(entities.Any(e => e.Kind == "order_id" && int.TryParse(e.Value, out var n) && n == id));
        if (!string.IsNullOrWhiteSpace(action.CustomerName))
            supplied.Add(input.Contains(action.CustomerName, StringComparison.OrdinalIgnoreCase));
        if (action.Amount is { } amount)
            supplied.Add(entities.Any(e => e.Kind == "number"
                && decimal.TryParse(e.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n == amount));
        if (!string.IsNullOrWhiteSpace(action.Status))
            supplied.Add(input.Contains(action.Status, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(action.Courier))
            supplied.Add(input.Contains(action.Courier, StringComparison.OrdinalIgnoreCase));

        return supplied.Count == 0 ? null : (double)supplied.Count(s => s) / supplied.Count;
    }
}
