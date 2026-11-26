namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Stage 3 merge. Candidates from the deterministic parser and the model are one list.
/// Same action and same arguments: one candidate, evidence merged by taking the strongest value of each feature (never added).
/// Same action, different arguments: kept as competing candidates. An argument the other source did not state (null) is not a difference.
/// Different actions: kept separately. A multi-action message is one plan candidate; its members are not also listed alone.
/// </summary>
public static class CandidateMerger
{
    /// <summary>
    /// One name per action. The deterministic parser names commands in CamelCase ("CancelOrder"), the model in snake_case ("cancel_order").
    /// Both are reduced to the snake_case form, so the two sources meet on the same candidate.
    /// </summary>
    public static string Canonical(string name) =>
        string.Concat(name.Select((ch, i) => char.IsUpper(ch) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(ch) : ch.ToString()));

    public static IReadOnlyList<ConversationCandidate> Merge(IEnumerable<ConversationCandidate> sources)
    {
        var merged = new List<ConversationCandidate>();
        foreach (var c in sources)
        {
            var i = merged.FindIndex(existing => SameCandidate(existing, c));
            if (i < 0) merged.Add(c);
            else merged[i] = Strongest(merged[i], c);
        }

        var inPlan = merged.Where(c => c.Plan is not null).SelectMany(c => c.Plan!).ToHashSet(StringComparer.Ordinal);
        return merged.Where(c => c.Plan is not null || !inPlan.Contains(c.Action)).ToList();
    }

    private static bool SameCandidate(ConversationCandidate a, ConversationCandidate b)
    {
        if (a.Plan is not null || b.Plan is not null)
            return a.Plan is not null && b.Plan is not null
                && a.Plan.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.Plan.OrderBy(x => x, StringComparer.Ordinal));

        return a.Action == b.Action && (a.Arguments is null || b.Arguments is null || a.Arguments == b.Arguments);
    }

    private static ConversationCandidate Strongest(ConversationCandidate a, ConversationCandidate b) => a with
    {
        Intent = Max(a.Intent, b.Intent),
        Pattern = Max(a.Pattern, b.Pattern),
        Entity = Max(a.Entity, b.Entity),
        Argument = Max(a.Argument, b.Argument),
        IsAlternative = a.IsAlternative && b.IsAlternative,
        IsDeterministic = a.IsDeterministic || b.IsDeterministic,
        Plan = a.Plan ?? b.Plan,
        Arguments = a.Arguments ?? b.Arguments,
    };

    private static double? Max(double? x, double? y) => x is null ? y : y is null ? x : Math.Max(x.Value, y.Value);
}
