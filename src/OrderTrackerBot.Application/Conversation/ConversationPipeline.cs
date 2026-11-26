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

public sealed record ConversationCandidate(string Action, int? Score, bool IsAlternative = false);

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
        var candidates = new List<ConversationCandidate>();

        if (command is not null)
            candidates.Add(new ConversationCandidate(command.Kind.ToString(), null));
        if (action is not null)
        {
            candidates.Add(new ConversationCandidate(action.Action, action.Score));
            candidates.AddRange(action.Alternatives.Select(a => new ConversationCandidate(a.Action, a.Score, true)));
        }

        if (candidates.Count == 0)
            return new(input, entities, candidates, Array.Empty<string>(), ConversationPipelineStage.ProduceCandidates, "no_candidate");

        // The engine's own rule (ConversationEngine.ActionChoice.cs), so the logged stage matches what the seller is asked.
        if (action is not null && ConversationEngine.NeedsActionChoice(action))
            return new(input, entities, candidates, Array.Empty<string>(), ConversationPipelineStage.Ambiguous, "close_candidates");

        var missing = MissingFieldsFor(action);
        if (missing.Count > 0)
            return new(input, entities, candidates, missing, ConversationPipelineStage.MissingFields, "required_fields_missing");

        return new(input, entities, candidates, Array.Empty<string>(), ConversationPipelineStage.SafetyValidation);
    }

    private static IReadOnlyList<ConversationEntity> ExtractEntities(string input)
    {
        var entities = new List<ConversationEntity>();
        entities.AddRange(Phone.Matches(input).Select(m => new ConversationEntity("phone", m.Value)));
        entities.AddRange(Number.Matches(input).Select(m => new ConversationEntity("number", m.Value)));
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
