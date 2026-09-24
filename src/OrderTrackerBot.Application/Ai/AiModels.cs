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
    /// <summary>instagram | whatsapp | tiktok | facebook | referral, when the message/screenshot shows where the order came from.</summary>
    public string? OrderSource { get; init; }
    public List<string> MissingRequiredFields { get; init; } = new();
}

/// <summary>
/// Result of one combined LLM call that does intent classification + order extraction +
/// confidence-based clarification in a single round trip (keeps latency and per-message
/// OpenAI cost down — this is the only AI call on the free-form "new order" path).
/// </summary>
public sealed class AiMessageAnalysis
{
    /// <summary>new_order | status_update | customer_feedback | support_query | off_topic | unclear.</summary>
    public string Intent { get; init; } = "";
    public bool IsOrderAttempt { get; init; }
    public AiOrderDraft? Order { get; init; }
    /// <summary>Further orders for other customers in the same message ("Ayesha 2 suit, Bilal 1 kurti").</summary>
    public List<AiOrderDraft> AdditionalOrders { get; init; } = new();
    /// <summary>Set for customer_feedback: the merchant relaying what a buyer thought of an order.</summary>
    public AiCustomerFeedback? Feedback { get; init; }
    /// <summary>Set for support_query: the seller forwarded a buyer's question ("mera order kab aayega?").</summary>
    public AiSupportQuery? SupportQuery { get; init; }
    /// <summary>Set when a screenshot is a payment receipt (JazzCash/Easypaisa/bank), not an order.</summary>
    public AiPaymentReceipt? Receipt { get; init; }
    /// <summary>True when the message named 2+ items in a way that could mean separate orders or one combined order (spec screen 3).</summary>
    public bool IsAmbiguousItemGrouping { get; init; }
    public string? ClarificationQuestion { get; init; }
    public List<string> ClarificationOptions { get; init; } = new();
}

public sealed class AiCustomerFeedback
{
    public required string CustomerName { get; init; }
    public required string Text { get; init; }
    /// <summary>positive | neutral | negative.</summary>
    public string? Sentiment { get; init; }
}

public sealed class AiSupportQuery
{
    public string? CustomerName { get; init; }
    public required string Question { get; init; }
}

/// <summary>Classification of an Instagram comment (section 6c) plus a drafted public reply for questions.</summary>
public sealed class AiCommentClassification
{
    /// <summary>order_interest | support_query | spam | unclear.</summary>
    public required string Intent { get; init; }
    public string? SuggestedReply { get; init; }
}

public sealed class AiPaymentReceipt
{
    public decimal? Amount { get; init; }
    public string? Provider { get; init; }
    public string? TransactionId { get; init; }
}

public sealed record AiImageInput(byte[] Bytes, string MimeType, string? Caption);

public interface IAiOrderAssistant
{
    Task<AiMessageAnalysis> AnalyzeMessageAsync(AiAnalysisContext context, string message, CancellationToken cancellationToken = default);

    /// <summary>Same analysis for a forwarded screenshot (Instagram/TikTok DM order, or a payment receipt).</summary>
    Task<AiMessageAnalysis> AnalyzeImageAsync(AiAnalysisContext context, AiImageInput image, CancellationToken cancellationToken = default);

    /// <summary>Classifies an IG comment. Returns null when AI is unavailable — callers fall back to keyword rules.</summary>
    Task<AiCommentClassification?> ClassifyCommentAsync(string businessName, string commentText, CancellationToken cancellationToken = default);

    /// <summary>Drafts a short reply the seller can forward to a buyer, grounded in the order facts given. Null when AI is unavailable.</summary>
    Task<string?> DraftSupportReplyAsync(string businessName, string question, string orderFacts, CancellationToken cancellationToken = default);

    /// <summary>Best-effort one-line insight appended to a trend/slow-mover report. Returns null if AI is unavailable — callers must not block on it.</summary>
    Task<string?> GenerateInsightAsync(string factsSummary, CancellationToken cancellationToken = default);
}
