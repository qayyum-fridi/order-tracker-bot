using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Ai;

namespace OrderTrackerBot.Application.Conversation;

public enum ConversationPipelineStage
{
    Normalize,
    ExtractEntities,
    ProduceCandidates,
    ResolveReferences,
    MissingFields,
    Ambiguous,
    SafetyValidation,
    Execute,
    RecordOutcome,
}

public sealed record ConversationEntity(string Kind, string Value);

/// <summary>
/// One action with its merged evidence (each feature 0 to 1, null = not applicable). Score is the composite of the features present.
/// Plan: the actions of a multi-action message, when this candidate is the whole plan (members are not also listed alone).
/// </summary>
public sealed record ConversationCandidate(
    string Action,
    double? Score,
    bool IsAlternative = false,
    bool IsDeterministic = false,
    double? Intent = null,
    double? Pattern = null,
    double? Entity = null,
    double? Context = null,
    double? Argument = null,
    IReadOnlyList<string>? Plan = null);

public sealed record ConversationPipelineResult(
    string Input,
    IReadOnlyList<ConversationEntity> Entities,
    IReadOnlyList<ConversationCandidate> Candidates,
    IReadOnlyList<string> MissingFields,
    ConversationPipelineStage Stage,
    string? Reason = null)
{
    public bool HasCandidates => Candidates.Count > 0;
    public bool IsAmbiguous => Stage == ConversationPipelineStage.Ambiguous;
    public bool IsReadyForValidation => Stage == ConversationPipelineStage.SafetyValidation;
}

/// <summary>Shared, side-effect-free stages before the ConversationEngine executes a command.</summary>
public static class ConversationPipeline
{
    private static readonly Regex Number = new(@"(?<![\p{L}\p{N}])\d+(?:[.,]\d+)?(?![\p{L}\p{N}])", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"(?<!\d)(?:\+?\d[\d -]{8,14}\d)(?!\d)", RegexOptions.Compiled);

    public static ConversationPipelineResult Analyze(string? rawInput, TurnAction? action = null, ParsedCommand? command = null, CandidateContext? context = null)
    {
        var input = InputTextNormalizer.Normalize(rawInput);
        var entities = ExtractEntities(input);

        // Stage 3: deterministic and model candidates, merged and scored.
        var generated = CandidateGenerationService.Generate(input, command, action, entities, context);
        var candidates = generated.Candidates;

        if (candidates.Count == 0)
            return new(input, entities, candidates, Array.Empty<string>(), ConversationPipelineStage.ProduceCandidates, "no_candidate");

        // Stage 5a: the same selector the engine uses before it asks "which one?", so the logged stage matches what the seller sees.
        var selection = CandidateSelector.Decide(candidates, generated.Blockers);
        if (selection.Decision != CandidateDecision.Select)
            return new(input, entities, candidates, Array.Empty<string>(), ConversationPipelineStage.Ambiguous, selection.Reason);

        // Stage 5b: the intent is settled; the arguments may still be missing.
        var missing = MissingFieldsFor(action);
        if (missing.Count > 0)
            return new(input, entities, candidates, missing, ConversationPipelineStage.MissingFields, "required_fields_missing");

        return new(input, entities, candidates, Array.Empty<string>(), ConversationPipelineStage.SafetyValidation);
    }

    /// <summary>Stage 3 + 5a for a model action alone (no deterministic command). The engine's "ask which one?" gate uses this.</summary>
    internal static CandidateSelection SelectAction(TurnAction action, string? rawInput, CandidateContext? context = null)
    {
        var input = InputTextNormalizer.Normalize(rawInput);
        var generated = CandidateGenerationService.Generate(input, null, action, ExtractEntities(input), context);
        return CandidateSelector.Decide(generated.Candidates, generated.Blockers);
    }

    // An order ID needs an explicit order marker ("order 12", "#12", "no. 12"). Any other number is a plain "number": never an order ID.
    private static readonly Regex OrderIdMarked = new(
        @"(?<![\p{L}])(?:orders?|odr|number|num|no|nambar|numbar|id|آرڈر|نمبر)\.?\s*[#:]?\s*(\d+)(?![\p{L}\p{N}])|#\s*(\d+)(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static IReadOnlyList<ConversationEntity> ExtractEntities(string input)
    {
        var entities = new List<ConversationEntity>();
        entities.AddRange(Phone.Matches(input).Select(m => new ConversationEntity("phone", m.Value)));

        var orderSpans = new List<(int Start, int End)>();
        foreach (Match m in OrderIdMarked.Matches(input))
        {
            var digits = m.Groups[1].Success ? m.Groups[1] : m.Groups[2];
            entities.Add(new ConversationEntity("order_id", digits.Value));
            orderSpans.Add((digits.Index, digits.Index + digits.Length));
        }

        foreach (Match m in Number.Matches(input))
        {
            if (orderSpans.Any(s => m.Index >= s.Start && m.Index < s.End)) continue;
            entities.Add(new ConversationEntity("number", m.Value));
        }
        return entities;
    }

    private static IReadOnlyList<string> MissingFieldsFor(TurnAction? action)
    {
        if (action is null) return Array.Empty<string>();

        return action.Action switch
        {
            TurnAction.CreateOrder when action.Orders.Count == 0 => ["order details"],
            TurnAction.AddProduct when action.Products.Count == 0 => ["product"],
            TurnAction.EditProduct when action.Products.Count == 0 || action.Products[0].Price is not > 0 => ["product and price"],
            TurnAction.AddStock when action.Products.Count == 0 => ["product and stock"],
            TurnAction.AddCustomer when action.Customer is null => ["customer"],
            TurnAction.EditCustomer when string.IsNullOrWhiteSpace(action.CustomerName) || action.Field is null || string.IsNullOrWhiteSpace(action.Value) => ["customer and changed detail"],
            TurnAction.UpdateStatus when action.Status is null => ["status"],
            TurnAction.RecordExpense or TurnAction.MonthlyExpense when action.Amount is not > 0 || string.IsNullOrWhiteSpace(action.Value) => ["expense and amount"],
            _ => Array.Empty<string>(),
        };
    }
}
