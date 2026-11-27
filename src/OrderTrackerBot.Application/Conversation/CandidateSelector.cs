namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Select: run the action. Incomplete: the intent is clear but required arguments are missing (ask for them, do not ask which action).
/// Clarify: several meanings or a contradiction (ask which one). Reject: nothing fits.
/// </summary>
public enum CandidateDecision { Select, Incomplete, Clarify, Reject }

public sealed record CandidateSelection(CandidateDecision Decision, ConversationCandidate? Top, double Margin, string Reason);

/// <summary>
/// Score cut-offs. These are provisional starting values, not measured confidence: tune them on labelled real seller messages
/// before treating any number as a calibrated limit.
/// </summary>
public sealed record SelectionThresholds(double SelectScore, double SelectMargin, double ClarifyScore)
{
    public static readonly SelectionThresholds Provisional = new(SelectScore: 0.85, SelectMargin: 0.15, ClarifyScore: 0.55);
}

/// <summary>
/// Stage 5a/5b. Chooses one candidate from the merged list by composite score, margin over the runner-up, and blockers.
/// Intent is judged first. A selectable intent with missing required arguments is Incomplete, not Select.
/// Only true alternative meanings reach Clarify; a plan whose steps contradict each other is blocked by <see cref="ActionPlanner"/>.
/// </summary>
public static class CandidateSelector
{
    /// <param name="blockers">Reasons not to choose on score alone (for example a contradictory order number).</param>
    /// <param name="missingFields">Required arguments of the chosen action that the message does not supply (null or empty = complete).</param>
    /// <param name="thresholds">Score cut-offs; defaults to the provisional values.</param>
    public static CandidateSelection Decide(
        IReadOnlyList<ConversationCandidate> candidates, IReadOnlyList<string>? blockers = null, IReadOnlyList<string>? missingFields = null,
        SelectionThresholds? thresholds = null)
    {
        var selection = DecideIntent(candidates, blockers, thresholds ?? SelectionThresholds.Provisional);
        if (selection.Decision == CandidateDecision.Select && missingFields is { Count: > 0 })
            return selection with { Decision = CandidateDecision.Incomplete, Reason = "required_fields_missing" };
        return selection;
    }

    private static CandidateSelection DecideIntent(IReadOnlyList<ConversationCandidate> candidates, IReadOnlyList<string>? blockers, SelectionThresholds t)
    {
        var ranked = candidates
            .Where(c => c.Score is not null)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.IsAlternative ? 1 : 0)
            .ToList();

        // Nothing scored: the model action is trusted, as before scoring existed.
        if (ranked.Count == 0)
            return new(CandidateDecision.Select, candidates.FirstOrDefault(), 1, "unscored_trusted");

        var top = ranked[0];
        var score = top.Score!.Value;
        var margin = score - (ranked.Count > 1 ? ranked[1].Score!.Value : 0);

        if (blockers is { Count: > 0 }) return new(CandidateDecision.Clarify, top, margin, blockers[0]);
        if (score < t.ClarifyScore) return new(CandidateDecision.Reject, top, margin, "low_confidence");
        if (score >= t.SelectScore && margin >= t.SelectMargin) return new(CandidateDecision.Select, top, margin, "clear");

        return new(CandidateDecision.Clarify, top, margin, margin < t.SelectMargin ? "close_candidates" : "below_select_score");
    }
}
