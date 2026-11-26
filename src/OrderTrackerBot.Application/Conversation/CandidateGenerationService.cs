using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Intents;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>What the deterministic parser made of the message.</summary>
public enum DeterministicOutcome
{
    /// <summary>A full command with its arguments. Conclusive on its own.</summary>
    Exact,
    /// <summary>The intent is recognised, but the parser cannot place it fully.</summary>
    Partial,
    /// <summary>More than one intent fits about equally well.</summary>
    Ambiguous,
    /// <summary>The parser recognises nothing.</summary>
    NoMatch,
}

/// <summary>The actions the seller was last offered (the open "which one?" question). Null = no such context.</summary>
public sealed record CandidateContext(IReadOnlyList<string> ExpectedActions);

/// <summary>
/// Stage 3 output. <see cref="Blockers"/> are reasons the selector must not choose on score alone (contradictions in the message).
/// <see cref="IsConclusive"/> is true when an exact command needs no model action to agree with it.
/// </summary>
public sealed record CandidateGenerationResult(
    IReadOnlyList<ConversationCandidate> Candidates,
    DeterministicOutcome Deterministic,
    IReadOnlyList<string> Blockers,
    bool IsConclusive);

/// <summary>
/// Stage 3 orchestrator. The deterministic parser and the model's action are candidate sources for the same normalized message.
/// The model's action is produced by the caller (ConversationEngine.ModelFirst); this service never calls it.
/// An exact deterministic command is kept as a candidate even when the model is present, so the two can still compete.
/// </summary>
public static class CandidateGenerationService
{
    /// <summary>Two intents within this share of the top word-overlap score are ambiguous rather than partial.</summary>
    private const double AmbiguousRatio = 0.8;
    private const int SuggestionCount = 2;

    public static CandidateGenerationResult Generate(
        string input, ParsedCommand? command, TurnAction? model, IReadOnlyList<ConversationEntity> entities, CandidateContext? context)
    {
        // A model action without a score is trusted as before: deterministic suggestions do not reopen it.
        var (outcome, deterministic) = Deterministic(input, command, model?.Score is not null);

        var sources = new List<ConversationCandidate>(deterministic);
        if (model is not null) sources.AddRange(ModelCandidates(model, entities, input));

        var candidates = CandidateMerger.Merge(sources).Select(c => Scored(c, context)).ToList();

        var blockers = new List<string>();
        if (model is not null && CandidateScorer.EntityAlignment(model, entities) == 0)
            blockers.Add("conflicting_reference");
        if (model?.Score is not null && CandidateScorer.ArgumentEvidence(model, entities, input) == 0)
            blockers.Add("unsupported_argument");

        var conclusive = outcome == DeterministicOutcome.Exact
            && (model is null || CandidateMerger.Canonical(model.Action) == CandidateMerger.Canonical(command!.Kind.ToString()));

        return new CandidateGenerationResult(candidates, outcome, blockers, conclusive);
    }

    /// <param name="hasModel">True when a scored model action is present (see <see cref="Generate"/>).</param>
    private static (DeterministicOutcome Outcome, List<ConversationCandidate> Candidates) Deterministic(string input, ParsedCommand? command, bool hasModel)
    {
        if (command is not null)
        {
            var exact = new ConversationCandidate(CandidateMerger.Canonical(command.Kind.ToString()), null,
                IsAlternative: hasModel, IsDeterministic: true, Intent: 1, Pattern: 1);
            return (DeterministicOutcome.Exact, new List<ConversationCandidate> { exact });
        }

        // Partial and ambiguous intents only add a second opinion on a model action. Without one they are not candidates (the caller asks AI).
        if (!hasModel) return (DeterministicOutcome.NoMatch, new List<ConversationCandidate>());

        var near = IntentCatalog.Closest(input, SuggestionCount);
        if (near.Count == 0 || near[0].Score <= 0) return (DeterministicOutcome.NoMatch, new List<ConversationCandidate>());

        var ambiguous = near.Count > 1 && near[1].Score >= near[0].Score * AmbiguousRatio;
        var candidates = near.Take(ambiguous ? SuggestionCount : 1)
            .Select(n => new ConversationCandidate(CandidateMerger.Canonical(n.Intent.Kind), null,
                IsAlternative: true, IsDeterministic: true, Pattern: CandidateScorer.PartialPattern))
            .ToList();
        return (ambiguous ? DeterministicOutcome.Ambiguous : DeterministicOutcome.Partial, candidates);
    }

    private static IEnumerable<ConversationCandidate> ModelCandidates(TurnAction model, IReadOnlyList<ConversationEntity> entities, string input)
    {
        var name = CandidateMerger.Canonical(model.Action);

        // An unscored action is trusted as before scoring existed: it is listed (for the logs) but has no features to rank by.
        if (model.Score is not { } score)
        {
            yield return new ConversationCandidate(name, null);
            yield break;
        }

        var intent = score / 10.0;
        var entity = CandidateScorer.EntityAlignment(model, entities);
        var argument = CandidateScorer.ArgumentEvidence(model, entities, input);

        // Several actions in one message are one plan. Its members score with the primary action's evidence (per-action scores are not sent).
        if (model.Also.Count > 0)
        {
            var plan = new List<string> { name };
            plan.AddRange(model.Also.Select(a => CandidateMerger.Canonical(a.Action)));
            yield return new ConversationCandidate(string.Join("+", plan.OrderBy(x => x, StringComparer.Ordinal)), null,
                Plan: plan, Intent: intent, Entity: entity, Argument: argument);
        }

        yield return new ConversationCandidate(name, null, Intent: intent, Entity: entity, Argument: argument, Arguments: ArgumentsKey(model));

        foreach (var alt in model.Alternatives)
            yield return new ConversationCandidate(CandidateMerger.Canonical(alt.Action), null, IsAlternative: true, Intent: alt.Score / 10.0);
    }

    /// <summary>The identifying arguments the action states, as one key. Null when it states none (so it is not a difference from another source).</summary>
    private static string? ArgumentsKey(TurnAction a)
    {
        var parts = new[] { a.OrderId?.ToString(), a.CustomerName, a.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture), a.Status, a.Courier };
        return parts.All(string.IsNullOrWhiteSpace) ? null : string.Join("|", parts.Select(p => p ?? ""));
    }

    /// <summary>Context support (1 = the seller was offered this action, 0 = offered others, null = no context) and the composite score.</summary>
    private static ConversationCandidate Scored(ConversationCandidate c, CandidateContext? context)
    {
        double? support = null;
        if (context is not null)
        {
            var offered = c.Plan is null
                ? context.ExpectedActions.Contains(c.Action)
                : c.Plan.Any(a => context.ExpectedActions.Contains(a));
            support = offered ? 1 : 0;
        }

        var withContext = c with { Context = support };
        return withContext with
        {
            Score = CandidateScorer.Composite(withContext.Intent, withContext.Pattern, withContext.Entity, withContext.Context, withContext.Argument),
        };
    }
}
