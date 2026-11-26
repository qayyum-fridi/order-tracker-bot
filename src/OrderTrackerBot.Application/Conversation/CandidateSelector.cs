namespace OrderTrackerBot.Application.Conversation;

public enum CandidateDecision { Select, Clarify, Reject }

public sealed record CandidateSelection(CandidateDecision Decision, ConversationCandidate? Top, double Margin, string Reason);

/// <summary>
/// Stage 5a. Chooses one candidate from the merged list by composite score, margin over the runner-up and conflict.
/// Intent confidence is judged here; missing arguments are judged later (Stage 5b), so a clear intent with missing
/// fields still selects. Thresholds are starting values to tune on labelled seller messages, not measured accuracy.
/// </summary>
public static class CandidateSelector
{
    public const double SelectScore = 0.85;
    public const double SelectMargin = 0.15;
    public const double ClarifyScore = 0.55;

    public static CandidateSelection Decide(IReadOnlyList<ConversationCandidate> candidates, bool conflict)
    {
        var ranked = candidates
            .Where(c => c.Score is not null)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.IsAlternative ? 1 : 0)
            .ToList();

        // Unscored model action: no evidence to doubt it, so it is trusted (as before scoring existed).
        if (ranked.Count == 0)
            return new(CandidateDecision.Select, candidates.FirstOrDefault(), 1, "unscored_trusted");

        var top = ranked[0];
        var score = top.Score!.Value;
        var margin = score - (ranked.Count > 1 ? ranked[1].Score!.Value : 0);

        // A contradiction in the message (e.g. two different order numbers) never runs on score alone.
        if (conflict) return new(CandidateDecision.Clarify, top, margin, "conflicting_reference");
        if (score < ClarifyScore) return new(CandidateDecision.Reject, top, margin, "low_confidence");
        if (score >= SelectScore && margin >= SelectMargin) return new(CandidateDecision.Select, top, margin, "clear");

        return new(CandidateDecision.Clarify, top, margin, margin < SelectMargin ? "close_candidates" : "below_select_score");
    }
}
