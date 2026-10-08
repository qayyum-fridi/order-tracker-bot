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
    /// <summary>Products the bot just asked the seller to price; a price in the next message is for these, not a new product.</summary>
    public IReadOnlyList<string> PendingPriceProducts { get; init; } = Array.Empty<string>();
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
    /// <summary>Delivery charge in rupees when the order states one ("delivery 250", "free delivery" = 0); null = not mentioned.
    /// Settable so a deterministic match on the seller's own text can override the model.</summary>
    public decimal? DeliveryCharge { get; set; }
    /// <summary>Advance the buyer already paid, found in the seller's text (not asked of the model).</summary>
    public decimal? AdvancePaid { get; set; }
    public List<string> MissingRequiredFields { get; init; } = new();
}

/// <summary>
/// Result of one combined LLM call that does intent classification + order extraction +
/// confidence-based clarification in a single round trip (keeps latency and per-message
/// OpenAI cost down — this is the only AI call on the free-form "new order" path).
/// </summary>
public sealed class AiMessageAnalysis
{
    /// <summary>new_order | status_update | customer_feedback | support_query | add_products | create_discount | off_topic | unclear.</summary>
    public string Intent { get; init; } = "";
    public bool IsOrderAttempt { get; init; }
    /// <summary>True when the AI could not be reached (no API key, or the call failed) — distinct from "reached it, not an order".</summary>
    public bool AiUnavailable { get; init; }
    public AiOrderDraft? Order { get; init; }
    /// <summary>Further orders for other customers in the same message ("Ayesha 2 suit, Bilal 1 kurti").</summary>
    public List<AiOrderDraft> AdditionalOrders { get; init; } = new();
    /// <summary>Set for customer_feedback: the merchant relaying what a buyer thought of an order.</summary>
    public AiCustomerFeedback? Feedback { get; init; }
    /// <summary>Set for support_query: the seller forwarded a buyer's question ("mera order kab aayega?").</summary>
    public AiSupportQuery? SupportQuery { get; init; }
    /// <summary>Set for add_products: the seller is telling the bot about products they sell (with or without prices), not placing an order.</summary>
    public List<AiNewProduct> NewProducts { get; init; } = new();
    /// <summary>Set when a screenshot is a payment receipt (JazzCash/Easypaisa/bank), not an order.</summary>
    public AiPaymentReceipt? Receipt { get; init; }
    /// <summary>True when the message named 2+ items in a way that could mean separate orders or one combined order (spec screen 3).</summary>
    public bool IsAmbiguousItemGrouping { get; init; }
    public string? ClarificationQuestion { get; init; }
    public List<string> ClarificationOptions { get; init; } = new();
}

/// <summary>A product the seller named while describing what they sell; the price is null when they did not say it.</summary>
public sealed class AiNewProduct
{
    public string Name { get; init; } = "";
    public decimal? Price { get; init; }
    /// <summary>What it cost the seller, when they said so.</summary>
    public decimal? Cost { get; init; }
    /// <summary>How many the seller has, when they said so.</summary>
    public int? Stock { get; init; }
    /// <summary>Other facts the seller gave ("fabric" = "cotton", "color" = "white"); empty when none.</summary>
    public Dictionary<string, string> Attributes { get; init; } = new();
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

    /// <summary>
    /// Translates bot messages from Roman Urdu into <paramref name="targetLanguage"/> (a <c>Lang</c> constant), one result per input in the
    /// same order. Null when AI is unavailable or the result is unusable — callers send the original text instead.
    /// </summary>
    Task<IReadOnlyList<string>?> TranslateAsync(IReadOnlyList<string> texts, string targetLanguage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites a voice-note transcript (often Urdu script, misheard words, spoken numbers) into the exact text the seller would have typed,
    /// given what the bot is currently waiting for. Null when AI is unavailable or the result is unusable — callers use the transcript as is.
    /// </summary>
    Task<AiVoiceInterpretation?> InterpretVoiceAsync(AiVoiceContext context, string transcript, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the seller's voice note means: the typed messages to run in order (e.g. "edit order 12", "price 1 = 1500", "done"), or a short
/// question to ask first when the intent is clear but a needed detail (which order, the new price) was not said.
/// </summary>
public sealed class AiVoiceInterpretation
{
    public List<string> Steps { get; init; } = new();
    /// <summary>The kind of each step (reply | command | yes | no | choose | edit_step | done | skip | help), same length as <see cref="Steps"/>;
    /// empty when the model gave plain strings. The engine drops any step whose action is not valid in the bot's current state.</summary>
    public List<string> Actions { get; init; } = new();
    public string? Question { get; init; }
}

/// <summary>What the bot is waiting for right now, so a voice transcript can be understood in context.</summary>
public sealed class AiVoiceContext
{
    public string BusinessName { get; init; } = "";
    /// <summary>English description of the bot's last question and the input it expects (including any numbered options).</summary>
    public string Situation { get; init; } = "";
    /// <summary>The only step kinds the bot accepts in this state; the model must pick from these.</summary>
    public List<string> AllowedActions { get; init; } = new();
    public List<string> CatalogNames { get; init; } = new();
    /// <summary>The seller's latest orders, one line each ("Order #12 Hassan, Pending: 1) Lawn Suit x1 @ Rs.1800 ...") so "Hassan ka order" can be resolved.</summary>
    public List<string> RecentOrders { get; init; } = new();
    /// <summary>The seller's saved customers with their order history ("Hassan, 0300…, Lahore: 3 orders, last Order #12 Pending"), so a spoken name can be matched or recognised as new.</summary>
    public List<string> KnownCustomers { get; init; } = new();
    /// <summary>The last few messages in the chat, oldest first ("Bot: …" / "Seller: …"), so a short spoken answer ("1500") can be tied to the question it answers.</summary>
    public List<string> RecentExchanges { get; init; } = new();
}
