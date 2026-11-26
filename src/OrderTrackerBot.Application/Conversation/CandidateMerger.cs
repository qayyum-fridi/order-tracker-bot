namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Stage 3 merge. Candidates from the deterministic parser and the model are one list: the same action is one candidate.
/// Evidence merges by taking the strongest value of each feature, never by adding (both sources judge the same message).
/// A multi-action message is one plan candidate, not competing interpretations: its members are not also listed alone.
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
        var order = new List<string>();
        var byKey = new Dictionary<string, ConversationCandidate>(StringComparer.Ordinal);
        foreach (var c in sources)
        {
            var key = KeyOf(c);
            if (byKey.TryGetValue(key, out var existing))
                byKey[key] = Strongest(existing, c);
            else
            {
                byKey[key] = c;
                order.Add(key);
            }
        }

        var merged = order.Select(k => byKey[k]).ToList();
        var inPlan = merged.Where(c => c.Plan is not null).SelectMany(c => c.Plan!).ToHashSet(StringComparer.Ordinal);
        return merged.Where(c => c.Plan is not null || !inPlan.Contains(c.Action)).ToList();
    }

    private static string KeyOf(ConversationCandidate c) =>
        c.Plan is null ? c.Action : "plan:" + string.Join("+", c.Plan.OrderBy(x => x, StringComparer.Ordinal));

    private static ConversationCandidate Strongest(ConversationCandidate a, ConversationCandidate b) => a with
    {
        Intent = Max(a.Intent, b.Intent),
        Pattern = Max(a.Pattern, b.Pattern),
        Entity = Max(a.Entity, b.Entity),
        Argument = Max(a.Argument, b.Argument),
        IsAlternative = a.IsAlternative && b.IsAlternative,
        IsDeterministic = a.IsDeterministic || b.IsDeterministic,
        Plan = a.Plan ?? b.Plan,
    };

    private static double? Max(double? x, double? y) => x is null ? y : y is null ? x : Math.Max(x.Value, y.Value);
}
