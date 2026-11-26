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
/// One action with its merged evidence (0-1 each, null = not applicable). Score is the composite of the evidence present.
/// Intent: model confidence, or 1 for an exact deterministic match. Pattern: 1 when the deterministic parser matched this action.
/// Entity: the order number in the action agrees with the number in the message (1), contradicts it (0), or there is none (null).
/// </summary>
public sealed record ConversationCandidate(
    string Action,
    double? Score,
    bool IsAlternative = false,
    bool IsDeterministic = false,
    double? Intent = null,
    double? Pattern = null,
    double? Entity = null);

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

    public static ConversationPipelineResult Analyze(string? rawInput, TurnAction? action = null, ParsedCommand? command = null)
    {
        var input = InputTextNormalizer.Normalize(rawInput);
        var entities = ExtractEntities(input);
        var candidates = ProduceCandidates(action, command, entities);

        if (candidates.Count == 0)
            return new(input, entities, candidates, Array.Empty<string>(), ConversationPipelineStage.ProduceCandidates, "no_candidate");

        // Stage 5a. Same selector the engine uses before it asks "which one?", so the logged stage matches what the seller sees.
        var selection = CandidateSelector.Decide(candidates, OrderReferenceConflicts(action, entities));
        if (selection.Decision != CandidateDecision.Select)
            return new(input, entities, candidates, Array.Empty<string>(), ConversationPipelineStage.Ambiguous, selection.Reason);

        var missing = MissingFieldsFor(action);
        if (missing.Count > 0)
            return new(input, entities, candidates, missing, ConversationPipelineStage.MissingFields, "required_fields_missing");

        return new(input, entities, candidates, Array.Empty<string>(), ConversationPipelineStage.SafetyValidation);
    }

    // Composite weights from the intent-understanding design. Context (0.15) and argument evidence (0.10) are not computed yet,
    // so the weights of the features that are present are renormalised: a missing feature neither helps nor hurts.
    private const double IntentWeight = 0.35;
    private const double PatternWeight = 0.20;
    private const double EntityWeight = 0.20;

    /// <summary>
    /// Stage 3. One candidate per action name. Sources are merged by taking the strongest value of each evidence feature,
    /// never by adding them (they all judge the same message). The deterministic command is an exact match, so it scores
    /// full intent and pattern evidence; it is an alternative when the model names another primary action.
    /// Alternatives only exist relative to a scored primary action, so an unscored primary carries none.
    /// </summary>
    internal static IReadOnlyList<ConversationCandidate> ProduceCandidates(TurnAction? action, ParsedCommand? command, IReadOnlyList<ConversationEntity> entities)
    {
        var list = new List<ConversationCandidate>();

        void Add(ConversationCandidate c)
        {
            var i = list.FindIndex(x => x.Action == c.Action);
            if (i < 0)
            {
                list.Add(c);
                return;
            }

            var e = list[i];
            list[i] = e with
            {
                Intent = Strongest(e.Intent, c.Intent),
                Pattern = Strongest(e.Pattern, c.Pattern),
                Entity = Strongest(e.Entity, c.Entity),
                IsAlternative = e.IsAlternative && c.IsAlternative,
                IsDeterministic = e.IsDeterministic || c.IsDeterministic,
            };
        }

        if (command is not null)
            Add(new ConversationCandidate(command.Kind.ToString(), null, IsAlternative: action is not null, IsDeterministic: true, Intent: 1, Pattern: 1));
        if (action is not null)
        {
            // Model confidence is 0-10. An action without a score is trusted (Intent stays null, so it is not ranked).
            Add(new ConversationCandidate(action.Action, null, Intent: action.Score / 10.0,
                Entity: action.Score is null ? null : EntityAlignment(action, entities)));
            if (action.Score is not null)
                foreach (var alt in action.Alternatives)
                    Add(new ConversationCandidate(alt.Action, null, IsAlternative: true, Intent: alt.Score / 10.0));
        }

        return list.Select(c => c with { Score = Composite(c.Intent, c.Pattern, c.Entity) }).ToList();
    }

    private static double? Strongest(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static double? Composite(double? intent, double? pattern, double? entity)
    {
        double sum = 0, weight = 0;
        if (intent is { } i) { sum += IntentWeight * i; weight += IntentWeight; }
        if (pattern is { } p) { sum += PatternWeight * p; weight += PatternWeight; }
        if (entity is { } e) { sum += EntityWeight * e; weight += EntityWeight; }
        return weight == 0 ? null : sum / weight;
    }

    private static double? EntityAlignment(TurnAction action, IReadOnlyList<ConversationEntity> entities)
    {
        if (action.OrderId is not { } id) return null;
        var numbers = entities.Where(e => e.Kind == "order_id").Select(e => int.TryParse(e.Value, out var n) ? n : -1).ToList();
        if (numbers.Count == 0) return null;
        return numbers.Contains(id) ? 1 : 0;
    }

    /// <summary>Stage 3 + 5a for a model action alone (no deterministic command). The engine's "ask which one?" gate uses this.</summary>
    internal static CandidateSelection SelectAction(TurnAction action, string? rawInput)
    {
        var entities = ExtractEntities(InputTextNormalizer.Normalize(rawInput));
        return CandidateSelector.Decide(ProduceCandidates(action, null, entities), OrderReferenceConflicts(action, entities));
    }

    /// <summary>The action names an order number that the message contradicts (it names other order numbers only).</summary>
    internal static bool OrderReferenceConflicts(TurnAction? action, IReadOnlyList<ConversationEntity> entities) =>
        action is not null && EntityAlignment(action, entities) is 0;

    // An order ID needs an explicit order marker ("order 12", "#12", "no. 12"). Any other number is a plain "number": never an order ID.
    private static readonly Regex OrderIdMarked = new(
        @"(?<![\p{L}])(?:orders?|odr|number|num|no|nambar|numbar|id|آرڈر|نمبر)\.?\s*[#:]?\s*(\d+)(?![\p{L}\p{N}])|#\s*(\d+)(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static IReadOnlyList<ConversationEntity> ExtractEntities(string input)
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
