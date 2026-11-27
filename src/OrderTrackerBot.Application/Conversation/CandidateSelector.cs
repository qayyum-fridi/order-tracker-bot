namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Select: run the action. Incomplete: the intent is clear but required arguments are missing (ask for them, do not ask which action).
/// Clarify: several meanings or a contradiction (ask which one). Reject: nothing fits.
/// </summary>
public enum CandidateDecision { Select, Incomplete, Clarify, Reject }

public sealed record CandidateSelection(CandidateDecision Decision, ConversationCandidate? Top, double Margin, string Reason);

/// <summary>
/// Stage 5a/5b. Chooses one candidate from the merged list by composite score, margin over the runner-up, and blockers.
/// Intent is judged first. A selectable intent with missing required arguments is Incomplete, not Select.
/// Thresholds are starting values to tune on labelled seller messages, not measured accuracy.
/// </summary>
public static class CandidateSelector
{
    public const double SelectScore = 0.85;
    public const double SelectMargin = 0.15;
    public const double ClarifyScore = 0.55;

    /// <param name="blockers">Reasons not to choose on score alone (for example a contradictory order number).</param>
    /// <param name="missingFields">Required arguments of the chosen action that the message does not supply (null or empty = complete).</param>
    public static CandidateSelection Decide(
        IReadOnlyList<ConversationCandidate> candidates, IReadOnlyList<string>? blockers = null, IReadOnlyList<string>? missingFields = null)
    {
        var selection = DecideIntent(candidates, blockers);
        if (selection.Decision == CandidateDecision.Select && missingFields is { Count: > 0 })
            return selection with { Decision = CandidateDecision.Incomplete, Reason = "required_fields_missing" };
        return selection;
    }

    private static CandidateSelection DecideIntent(IReadOnlyList<ConversationCandidate> candidates, IReadOnlyList<string>? blockers)
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
        if (score < ClarifyScore) return new(CandidateDecision.Reject, top, margin, "low_confidence");
        if (score >= SelectScore && margin >= SelectMargin) return new(CandidateDecision.Select, top, margin, "clear");

        return new(CandidateDecision.Clarify, top, margin, margin < SelectMargin ? "close_candidates" : "below_select_score");
    }
}
