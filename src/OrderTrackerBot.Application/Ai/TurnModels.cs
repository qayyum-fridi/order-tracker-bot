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
    public const string ShowCatalog = "show_catalog";
    public const string Undo = "undo";
    public const string HelpOrChat = "help_or_chat";
    public const string OutOfScope = "out_of_scope";

    /// <summary>Every action the model may pick. Anything else in a reply is discarded.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        CreateOrder, UpdateStatus, CancelOrder, ShowOrders, ShowOrder, ShowSummary, AddProduct, ShowCatalog, Undo, HelpOrChat, OutOfScope
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
