namespace OrderTrackerBot.Application.Conversation;

/// <summary>One step of a plan: what it does (<see cref="Effect"/>) to one record (<see cref="EntityKey"/>, e.g. "order:12", "customer:Sara").</summary>
public sealed record PlannedStep(string Action, string EntityKey, PlanEffect Effect);

/// <summary>What a step does to its record. Opposite effects on the same record cannot both run.</summary>
public enum PlanEffect { Create, Edit, Advance, Pay, Cancel, Remove, Restore }

/// <summary>Two steps on the same record whose effects contradict each other, by position in the plan.</summary>
public sealed record PlanConflict(int First, int Second, string EntityKey, PlanEffect A, PlanEffect B);

/// <summary>
/// Stage 5a. An action plan keeps its steps in the order the seller said them. Dependencies are the same-record pairs
/// (an earlier step must run before a later one on the same record). A plan with a contradictory pair is blocked, not executed.
/// The contradiction table is a starting set; extend it with the real conversations that need it.
/// </summary>
public static class ActionPlanner
{
    private static readonly HashSet<(PlanEffect, PlanEffect)> Contradictions = new()
    {
        (PlanEffect.Cancel, PlanEffect.Advance), (PlanEffect.Cancel, PlanEffect.Pay),
        (PlanEffect.Remove, PlanEffect.Edit), (PlanEffect.Remove, PlanEffect.Advance), (PlanEffect.Remove, PlanEffect.Pay),
        (PlanEffect.Create, PlanEffect.Remove), (PlanEffect.Create, PlanEffect.Cancel),
    };

    public static bool Contradicts(PlanEffect a, PlanEffect b) => Contradictions.Contains((a, b)) || Contradictions.Contains((b, a));

    /// <summary>
    /// The dependency edges: (earlier, later) pairs of steps on the same record. Steps on different records are independent.
    /// </summary>
    public static IReadOnlyList<(int From, int To)> Dependencies(IReadOnlyList<PlannedStep> steps)
    {
        var edges = new List<(int, int)>();
        for (var i = 0; i < steps.Count; i++)
            for (var j = i + 1; j < steps.Count; j++)
                if (steps[i].EntityKey == steps[j].EntityKey) edges.Add((i, j));
        return edges;
    }

    /// <summary>The plan's conflicts: same record, contradictory effects. Empty = the plan may run, in its given order.</summary>
    public static IReadOnlyList<PlanConflict> FindConflicts(IReadOnlyList<PlannedStep> steps) =>
        Dependencies(steps)
            .Where(e => Contradicts(steps[e.From].Effect, steps[e.To].Effect))
            .Select(e => new PlanConflict(e.From, e.To, steps[e.From].EntityKey, steps[e.From].Effect, steps[e.To].Effect))
            .ToList();
}
