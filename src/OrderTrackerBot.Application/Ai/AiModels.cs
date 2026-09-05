namespace OrderTrackerBot.Application.Ai;

public sealed class AiCatalogItem
{
    public required string Name { get; init; }
    public decimal Price { get; init; }
}

/// <summary>Everything the model needs to ground its answer in this seller's real data.</summary>
public sealed class AiAnalysisContext
{
    public required string BusinessName { get; init; }
    public required IReadOnlyList<AiCatalogItem> Catalog { get; init; }
    public string PreferredLanguage { get; init; } = "roman-urdu";
}

public sealed class AiOrderItemDraft
{
    public required string ProductName { get; init; }
    public int Quantity { get; init; } = 1;
    /// <summary>Set when the model matched an existing catalog entry (possibly informal/misspelled name).</summary>
    public string? MatchedCatalogProductName { get; init; }
}

public sealed class AiOrderDraft
{
    public string? CustomerName { get; init; }
    public List<AiOrderItemDraft> Items { get; init; } = new();
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? PaymentMethod { get; init; }
    public string? DiscountCode { get; init; }
    public List<string> MissingRequiredFields { get; init; } = new();
}

/// <summary>
/// Result of one combined LLM call that does intent classification + order extraction +
/// confidence-based clarification in a single round trip (keeps latency and per-message
/// OpenAI cost down — this is the only AI call on the free-form "new order" path).
/// </summary>
public sealed class AiMessageAnalysis
{
    public bool IsOrderAttempt { get; init; }
    public AiOrderDraft? Order { get; init; }
    /// <summary>True when the message named 2+ items in a way that could mean separate orders or one combined order (spec screen 3).</summary>
    public bool IsAmbiguousItemGrouping { get; init; }
    public string? ClarificationQuestion { get; init; }
    public List<string> ClarificationOptions { get; init; } = new();
}

public interface IAiOrderAssistant
{
    Task<AiMessageAnalysis> AnalyzeMessageAsync(AiAnalysisContext context, string message, CancellationToken cancellationToken = default);

    /// <summary>Best-effort one-line insight appended to a trend/slow-mover report. Returns null if AI is unavailable — callers must not block on it.</summary>
    Task<string?> GenerateInsightAsync(string factsSummary, CancellationToken cancellationToken = default);
}
