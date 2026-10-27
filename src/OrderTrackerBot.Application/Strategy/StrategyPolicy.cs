namespace OrderTrackerBot.Application.Strategy;

/// <summary>
/// Chooses when the bot may act without asking, from the seller's own outcomes. A contextual bandit with a pessimistic rule:
/// an answer is used without a question only when the lower bound (Wilson, 95%) of its share of all decisions in that context
/// reaches <see cref="AutoMinLowerBound"/>. Corrections count against it, so a seller who keeps fixing an auto answer is asked again.
/// Exploration is the question itself: there is no auto-apply for an answer the seller has not given often enough.
/// </summary>
public static class StrategyPolicy
{
    public const double AutoMinLowerBound = 0.8;

    private const double Z = 1.96;

    /// <param name="Arm">The answer, e.g. "catalog".</param>
    /// <param name="Good">Times it was the final choice and not corrected.</param>
    /// <param name="Total">Every decision in the context, whatever the answer.</param>
    public readonly record struct Count(string Arm, int Good, int Total);

    /// <summary>Wilson score lower bound of good / total (0 when there is no evidence).</summary>
    public static double LowerBound(int good, int total)
    {
        if (total <= 0) return 0;
        var n = (double)total;
        var p = Math.Clamp(good / n, 0, 1);
        var z2 = Z * Z;
        var centre = p + z2 / (2 * n);
        var margin = Z * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n));
        return Math.Max(0, (centre - margin) / (1 + z2 / n));
    }

    /// <summary>The arm to apply without asking, or null when none is reliable enough. The best lower bound wins.</summary>
    public static string? Pick(IEnumerable<Count> arms, double minLowerBound = AutoMinLowerBound) =>
        arms.Select(a => (a.Arm, Bound: LowerBound(a.Good, a.Total)))
            .Where(x => x.Bound >= minLowerBound)
            .OrderByDescending(x => x.Bound)
            .Select(x => x.Arm)
            .FirstOrDefault();
}
