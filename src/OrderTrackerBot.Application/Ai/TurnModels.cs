namespace OrderTrackerBot.Application.Ai;

/// <summary>
/// What one message (typed, or a voice transcript) MEANS: one action from a closed list plus its typed fields.
/// The model never writes a command; the engine runs the action by calling its handlers with these fields, after checking them against the database.
/// </summary>
public sealed class TurnAction
{
    public const string CreateOrder = "create_order";
    public const string UpdateStatus = "update_status";
    public const string CancelOrder = "cancel_order";
    public const string ShowOrders = "show_orders";
    public const string ShowOrder = "show_order";
    public const string ShowSummary = "show_summary";
    public const string AddProduct = "add_product";
    public const string EditProduct = "edit_product";
    public const string AddStock = "add_stock";
    public const string ShowCatalog = "show_catalog";
    public const string AddCustomer = "add_customer";
    public const string EditCustomer = "edit_customer";
    public const string EditOrder = "edit_order";
    public const string ExchangeSize = "exchange_size";
    public const string ShowCustomer = "show_customer";
    public const string HowTo = "how_to";
    public const string ContactSupport = "contact_support";
    public const string Clarify = "clarify";
    public const string Undo = "undo";
    public const string HelpOrChat = "help_or_chat";
    public const string OutOfScope = "out_of_scope";

    /// <summary>Every action the model may pick. Anything else in a reply is discarded.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        CreateOrder, UpdateStatus, CancelOrder, ShowOrders, ShowOrder, ShowSummary, AddProduct, EditProduct, AddStock, ShowCatalog,
        AddCustomer, EditCustomer, EditOrder, ExchangeSize, ShowCustomer, HowTo, ContactSupport, Clarify, Undo, HelpOrChat, OutOfScope
    };

    /// <summary>Most actions one message may carry. Extra ones beyond this are dropped by the parser.</summary>
    public const int MaxActionsPerMessage = 5;

    /// <summary>edit_customer: the one detail of a saved customer that changes.</summary>
    public static readonly IReadOnlyList<string> CustomerFields = new[] { "phone", "address", "city", "name" };

    /// <summary>edit_order: one change to a saved order's items. set_quantity / set_price / remove name an item; add names a product and a quantity.</summary>
    public static readonly IReadOnlyList<string> OrderEditOps = new[] { "set_quantity", "remove", "add", "set_price" };

    /// <summary>exchange_size: the item of a saved order whose size or colour changes, as the seller named the product. The new variant is <see cref="Value"/>.</summary>
    public string? ItemName { get; init; }

    /// <summary>how_to: what the seller asks how to do. "other" = something else.</summary>
    public static readonly IReadOnlyList<string> HowToTopics = new[]
    {
        AddProduct, EditProduct, AddStock, ShowCatalog, AddCustomer, EditCustomer, ShowCustomer, CreateOrder, UpdateStatus, CancelOrder, ShowOrders, ShowSummary, Undo, "other"
    };

    public static readonly IReadOnlyList<string> Statuses = new[] { "shipped", "delivered", "returned", "pending" };

    /// <summary>Report windows the model may name. The engine maps each to a period the report code already understands.</summary>
    public static readonly IReadOnlyList<string> Periods = new[]
    {
        "today", "yesterday", "this_week", "last_week", "this_month", "last_month", "last_7_days", "last_30_days"
    };

    public string Action { get; init; } = HelpOrChat;

    /// <summary>create_order: one entry per customer.</summary>
    public List<AiOrderDraft> Orders { get; init; } = new();

    /// <summary>add_product: the products the seller described.</summary>
    public List<AiNewProduct> Products { get; init; } = new();

    /// <summary>update_status / cancel_order / show_order: the order number the seller said, when they said one.</summary>
    public int? OrderId { get; init; }

    /// <summary>update_status / cancel_order / show_order / show_orders: the customer the seller named instead of (or besides) a number.</summary>
    public string? CustomerName { get; init; }

    /// <summary>update_status: one of <see cref="Statuses"/>.</summary>
    public string? Status { get; init; }

    /// <summary>show_orders / show_summary: one of <see cref="Periods"/>; null = today.</summary>
    public string? Period { get; init; }

    /// <summary>show_orders: true when the seller asked for orders still waiting to be sent.</summary>
    public bool PendingOnly { get; init; }

    /// <summary>add_customer: the buyer to save.</summary>
    public AiNewCustomer? Customer { get; init; }

    /// <summary>edit_customer: one of <see cref="CustomerFields"/>, with <see cref="Value"/> as its new value and <see cref="CustomerName"/> as the customer.</summary>
    public string? Field { get; init; }
    public string? Value { get; init; }

    /// <summary>how_to: one of <see cref="HowToTopics"/>.</summary>
    public string? Topic { get; init; }

    /// <summary>clarify: the one short question to ask the seller, in simple Roman Urdu.</summary>
    public string? Question { get; init; }

    /// <summary>edit_order: the changes to the order's items, in the order the seller said them.</summary>
    public List<AiOrderEdit> OrderEdits { get; init; } = new();

    /// <summary>
    /// The further actions the same message asked for ("Ali ka order bhej do aur Sara ka phone 0300..."), in the order said.
    /// Only the first action is held in the top-level object; the parser fills this list, and the engine runs them one after another.
    /// </summary>
    public List<TurnAction> Also { get; init; } = new();
}

/// <summary>One change to a saved order's items, as the seller said it. Item is the product name as spoken; the engine finds its line in the order.</summary>
public sealed class AiOrderEdit
{
    public string Op { get; init; } = "";
    public string? Item { get; init; }
    public int? Quantity { get; init; }
    public decimal? Price { get; init; }
}

/// <summary>What the model is shown besides the message, so its answer is about THIS seller's shop.</summary>
public sealed class TurnContext
{
    public string BusinessName { get; init; } = "";
    public string? ShopType { get; init; }
    /// <summary>The products most likely meant by this message (by sound and spelling), not the whole catalog: a shop can have hundreds.</summary>
    public IReadOnlyList<AiCatalogItem> Catalog { get; init; } = Array.Empty<AiCatalogItem>();
    /// <summary>How many products the shop has in all, so the model knows the list above is a part.</summary>
    public int CatalogSize { get; init; }
    public IReadOnlyList<string> RecentOrders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> KnownCustomers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RecentExchanges { get; init; } = Array.Empty<string>();
    /// <summary>True when the text is speech-to-text of a voice note (misheard words, no punctuation, numbers in words).</summary>
    public bool FromVoice { get; init; }
}

/// <summary>The single understanding step. Null = the model is unavailable or its answer was unusable: the caller falls back to the older pipeline.</summary>
public interface ITurnInterpreter
{
    /// <summary>Reads the text (typed, or a voice transcript) and, for a screenshot, the picture too. <paramref name="image"/> null = text only.</summary>
    Task<TurnAction?> UnderstandAsync(TurnContext context, string text, AiImageInput? image = null, CancellationToken cancellationToken = default);
}
