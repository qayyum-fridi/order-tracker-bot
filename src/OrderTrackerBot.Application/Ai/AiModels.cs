using OrderTrackerBot.Application.Intents;

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
    /// <summary>Features:AiCancelUndo. When true the reader may answer cancel_order / undo, and reads number words as digits. False = the original prompt and schema.</summary>
    public bool CancelUndoIntents { get; init; }
}

public sealed class AiOrderItemDraft
{
    public required string ProductName { get; init; }
    public int Quantity { get; init; } = 1;
    /// <summary>Set when the model matched an existing catalog entry (possibly informal/misspelled name).</summary>
    public string? MatchedCatalogProductName { get; init; }
    /// <summary>The price of one unit as the seller wrote it in the order ("Lawn Suit 3500"); null when none was written. Only compared with the catalog price, never used as the price unasked.</summary>
    public decimal? StatedUnitPrice { get; init; }
    /// <summary>True when <see cref="Quantity"/> counts whole packs ("2 carton tel"), not pieces; <see cref="StatedUnitPrice"/> is then the price of one pack.</summary>
    public bool InPacks { get; init; }
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
    /// <summary>True when the delivery charge was found in the message text by the deterministic parser (strategy learning).</summary>
    public bool DeliveryWrittenInText { get; set; }
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
    /// <summary>new_order | status_update | customer_feedback | support_query | add_products | add_customer | create_discount | off_topic | unclear, plus cancel_order | undo when <see cref="AiAnalysisContext.CancelUndoIntents"/> is on.</summary>
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
    /// <summary>Set for add_customer: a buyer the seller is saving before any order (name, and phone / address / city when said).</summary>
    public AiNewCustomer? NewCustomer { get; init; }
    /// <summary>Set when a screenshot is a payment receipt (JazzCash/Easypaisa/bank), not an order.</summary>
    public AiPaymentReceipt? Receipt { get; init; }
    /// <summary>True when the message named 2+ items in a way that could mean separate orders or one combined order (spec screen 3).</summary>
    public bool IsAmbiguousItemGrouping { get; init; }
    public string? ClarificationQuestion { get; init; }
    public List<string> ClarificationOptions { get; init; } = new();
}

/// <summary>A product the seller named while describing what they sell; the price is null when they did not say it.</summary>
/// <summary>The AI's reading of a reply to a numbered list: the 1-based option (0 = none) and the exact words of the reply it based that on.</summary>
/// <summary>What the router is told about THIS seller besides the message: the kind of shop, some of its products, what the bot was doing and the last few messages.</summary>
public sealed class AiRouteContext
{
    public string? ShopType { get; init; }
    public List<string> ProductNames { get; init; } = new();
    public string? Situation { get; init; }
    public List<string> RecentMessages { get; init; } = new();
}

public sealed class AiChoiceReading
{
    public int Option { get; init; }
    public string Quote { get; init; } = "";
}

/// <summary>The AI's reading of a reply to a yes / no question: "yes", "no" or "other", plus the exact words of the reply it based that on.</summary>
public sealed class AiYesNoReading
{
    public string Answer { get; init; } = "other";
    public string Quote { get; init; } = "";
}

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
    /// <summary>A price range the seller gave ("X se Y tak"): its low and high end. <see cref="Price"/> stays null, since one listing has one price.</summary>
    public decimal? PriceLow { get; init; }
    public decimal? PriceHigh { get; init; }
    /// <summary>Several colours / sizes the seller named for this one product (variants), each in the seller's own words; each combination becomes a listing.</summary>
    public List<string> Colors { get; init; } = new();
    /// <summary>The crossed-out (old) price the page shows next to the current price; null when none is shown.</summary>
    public decimal? OldPrice { get; init; }
    /// <summary>Standard sizes the product comes in (XS, S, M, L ...), normalised; one listing per size is made.</summary>
    public List<string> Sizes { get; init; } = new();
    /// <summary>
    /// For each number above, the exact words the seller used to say it (same spelling and script as in their message), or null. The bot checks that the words really
    /// are in the message, so a number needs no entry in a word list to be believed, and a number the model made up has no words to show.
    /// </summary>
    public string? PriceWords { get; init; }
    public string? CostWords { get; init; }
    public string? StockWords { get; init; }
    public string? PriceLowWords { get; init; }
    public string? PriceHighWords { get; init; }
    /// <summary>The bigger pack this product comes in ("carton", "box", "peti", "dozen"), when the seller named one. See <c>ProductPack</c>.</summary>
    public string? PackName { get; init; }
    /// <summary>How many pieces one pack holds.</summary>
    public int? PackQty { get; init; }
    /// <summary>What the seller PAYS for one whole pack.</summary>
    public decimal? PackCost { get; init; }
    /// <summary>What the seller SELLS one whole pack for.</summary>
    public decimal? PackPrice { get; init; }
    /// <summary>How many whole packs the seller has (or received).</summary>
    public int? StockPacks { get; init; }
}

/// <summary>A buyer the seller is saving before any order; phone, address and city are null when the seller did not say them.</summary>
public sealed class AiNewCustomer
{
    public string Name { get; init; } = "";
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? City { get; init; }
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

/// <summary>What was read off a picture once its type is known. Only the fields of that type are filled: expense = Amount + Note, wages = Name + Amount, customer = Name + Phone + Address + City.</summary>
public sealed class AiPictureReading
{
    public string? Name { get; set; }
    public decimal? Amount { get; set; }
    public string? Note { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    /// <summary>For a price list: every item with a clear price (Cost only when the picture shows what it cost).</summary>
    public List<AiPictureProduct> Products { get; set; } = new();
}

public sealed class AiPictureProduct
{
    public string? Name { get; set; }
    public decimal? Price { get; set; }
    public decimal? Cost { get; set; }
}

public interface IAiOrderAssistant
{
    Task<AiMessageAnalysis> AnalyzeMessageAsync(AiAnalysisContext context, string message, CancellationToken cancellationToken = default);

    /// <summary>Same analysis for a forwarded screenshot (Instagram/TikTok DM order, or a payment receipt).</summary>
    Task<AiMessageAnalysis> AnalyzeImageAsync(AiAnalysisContext context, AiImageInput image, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a picture the seller labelled as <paramref name="kind"/> ("expense", "wages" or "customer") for that type's fields only; the seller's own words
    /// (<see cref="AiImageInput.Caption"/>) win over the picture. Null when AI is unavailable or nothing could be read.
    /// </summary>
    Task<AiPictureReading?> ReadPictureAsync(string kind, AiImageInput image, CancellationToken cancellationToken = default) => Task.FromResult<AiPictureReading?>(null);

    /// <summary>Classifies an IG comment. Returns null when AI is unavailable — callers fall back to keyword rules.</summary>
    Task<AiCommentClassification?> ClassifyCommentAsync(string businessName, string commentText, CancellationToken cancellationToken = default);

    /// <summary>Drafts a short reply the seller can forward to a buyer, grounded in the order facts given. Null when AI is unavailable.</summary>
    Task<string?> DraftSupportReplyAsync(string businessName, string question, string orderFacts, CancellationToken cancellationToken = default);

    /// <summary>Best-effort one-line insight appended to a trend/slow-mover report. Returns null if AI is unavailable — callers must not block on it.</summary>
    Task<string?> GenerateInsightAsync(string factsSummary, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bot asked the seller a question (<paramref name="situation"/>) and the reply is not a clean answer: what is it? Returns "answer" (it answers the
    /// question), "new_order" (a fresh order / items, not an answer), "cancel" (drop the current order), "skip" (the asked phone is not known now and will be given later: save without it) or "other"; null when AI is unavailable (callers keep the old behaviour).
    /// </summary>
    Task<string?> ClassifyMidFlowReplyAsync(string situation, string message, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    /// <summary>
    /// A loose rule guessed that the message is the bot command <paramref name="commandKind"/>: does it really ask for that? <c>false</c> = it is something else
    /// (typically a new order or another request), so the AI order reader should handle it; <c>null</c> = unknown / no AI (keep the rule's answer).
    /// </summary>
    Task<bool?> ConfirmCommandMatchAsync(string message, string commandKind, CancellationToken cancellationToken = default) => Task.FromResult<bool?>(null);

    /// <summary>
    /// The bot asked a yes / no question (<paramref name="situation"/>) and the reply is not one of the plain yes / no words: does it say yes, no, or neither? The model also
    /// quotes the exact words of the reply that say so; the engine only acts when that quote really is in the message. Null = unknown / no AI.
    /// </summary>
    Task<AiYesNoReading?> ReadYesNoWithQuoteAsync(string situation, string message, CancellationToken cancellationToken = default) => Task.FromResult<AiYesNoReading?>(null);

    /// <summary>
    /// The bot showed a numbered list (<paramref name="options"/>) and the reply is not a number: which option does it mean? 1-based, 0 = none of them / unclear / another request.
    /// The model also quotes the exact words of the reply it based that on; the engine acts only when the quote is really in the message. Null = unknown / no AI.
    /// </summary>
    Task<AiChoiceReading?> ReadChoiceWithQuoteAsync(string situation, IReadOnlyList<string> options, string message, CancellationToken cancellationToken = default) => Task.FromResult<AiChoiceReading?>(null);

    /// <summary>A seller once taught that <paramref name="taughtPhrase"/> means a command; they now wrote <paramref name="newPhrase"/>. Do both ask for the same thing? <c>null</c> = unknown / no AI.</summary>
    Task<bool?> ConfirmSamePhraseAsync(string taughtPhrase, string newPhrase, CancellationToken cancellationToken = default) => Task.FromResult<bool?>(null);

    /// <summary>
    /// Picks ONE action from <paramref name="catalog"/> for a message the order reader could not place, and fills its slots from the words said.
    /// Null when AI is unavailable or the answer is unusable. The engine re-checks the id and the slots; the model never writes a command.
    /// </summary>
    Task<IntentRoute?> RouteIntentWithContextAsync(string message, IReadOnlyList<IntentDefinition> catalog, AiRouteContext context, CancellationToken cancellationToken = default) =>
        RouteIntentAsync(message, catalog, cancellationToken);

    Task<IntentRoute?> RouteIntentAsync(string message, IReadOnlyList<IntentDefinition> catalog, CancellationToken cancellationToken = default) =>
        Task.FromResult<IntentRoute?>(null);

    /// <summary>
    /// Plans what to do for a message the catalog could not place: ordered tool steps, a question, or a promise of more input. <paramref name="sellerContext"/>
    /// is <c>SellerContext.Render()</c>. Nothing runs here; the engine verifies every step first. Null when AI is unavailable or the answer is unusable.
    /// </summary>
    Task<OrderTrackerBot.Application.Agent.AgentPlan?> PlanAsync(string message, string sellerContext, IReadOnlyList<OrderTrackerBot.Application.Agent.AgentTool> tools, CancellationToken cancellationToken = default) =>
        Task.FromResult<OrderTrackerBot.Application.Agent.AgentPlan?>(null);

    /// <summary>
    /// What a spoken or typed answer MEANS for a yes / no question the bot just asked: "yes", "no" or "other" (a different request, unclear, or both).
    /// Meaning, not words, so any dialect or filler works. The model can answer only these three; it cannot make up a step. Null when AI is unavailable.
    /// </summary>
    Task<string?> ClassifyYesNoAsync(string question, string answer, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// What KIND of thing a voice transcript is, so each kind goes to the narrowest step: "command" (asks the bot to show or change something in the shop),
    /// "order" (a customer order being dictated), "chat" (a greeting, small talk, anything unrelated to the shop) or "other" (unclear). Meaning, not words.
    /// Null when AI is unavailable.
    /// </summary>
    Task<string?> ClassifyVoiceKindAsync(string transcript, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    /// <summary>Embedding vectors (meaning, not words) for the texts, same order; null when unavailable. Used to find a taught phrase that MEANS the same as a new sentence.</summary>
    Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<float[]>?>(null);

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
    /// <summary>The words of the transcript each step came from ("" when a step comes from the chat context only), same length as <see cref="Steps"/>;
    /// empty when the model gave no spans. A step with a span is checked against the transcript before it runs.</summary>
    public List<string> Spans { get; init; } = new();
    public string? Question { get; init; }
    /// <summary>When <see cref="Question"/> has a few known answers (which customer, which order, which status, which period): each is the exact text the bot
    /// receives when the seller taps it. Shown as buttons / a list so the seller can tap instead of speaking again.</summary>
    public List<string> Options { get; init; } = new();
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
