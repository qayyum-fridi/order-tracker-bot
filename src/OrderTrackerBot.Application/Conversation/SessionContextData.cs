using System.Text.Json;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public sealed class PendingOrderItemData
{
    public required string ProductName { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public int? ProductId { get; set; }
    /// <summary>piece/kg/... and pack size of the matched catalog listing, for "Sugar (5kg pack) x2 = 10kg total".</summary>
    public string UnitType { get; set; } = "piece";
    public decimal UnitQty { get; set; } = 1;
    /// <summary>True when UnitPrice came from a wholesale price tier (quantity is then in UnitType units, e.g. kg).</summary>
    public bool IsTierPrice { get; set; }
    public decimal? TierMinQty { get; set; }
    /// <summary>The unit price the seller wrote when it differs from the catalog price; cleared once they chose (price guard question).</summary>
    public decimal? StatedPrice { get; set; }
    /// <summary>The unit price the seller typed for a plain piece listing; compared with the buyer's price group price, never with the regular price alone.</summary>
    public decimal? WrittenPrice { get; set; }
    /// <summary>True once the seller answered the price question (1 or 3): the line keeps its price and is not re-priced for the buyer's group.</summary>
    public bool PriceDecided { get; set; }
}

public sealed class PendingOrderData
{
    public string? CustomerName { get; set; }
    public List<PendingOrderItemData> Items { get; set; } = new();
    public string? Phone { get; set; }
    /// <summary>The seller said the phone is not known yet and will be added later ("Hamira ka phone 0300…"): the order is saved without one.</summary>
    public bool PhoneLater { get; set; }
    public string? Address { get; set; }
    public string? PaymentMethodText { get; set; }
    public string? DiscountCode { get; set; }
    public string? OrderSource { get; set; }
    public bool FromScreenshot { get; set; }
    /// <summary>Counter (offline) sale: no phone needed, saved as delivered and fully paid.</summary>
    public bool PaidInFull { get; set; }
    /// <summary>When the sale really happened (UTC), for entries made later ("kal"); null = now.</summary>
    public DateTime? SoldAt { get; set; }
    public decimal DiscountAmount { get; set; }
    /// <summary>Null until set: the seller's default delivery charge is applied when totals are computed.</summary>
    public decimal? DeliveryCharge { get; set; }
    public decimal? AdvancePaid { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Total { get; set; }
    public int? DuplicateOfOrderId { get; set; }
    /// <summary>Instagram comment lead this order converts (screen 5d-7); marked converted when the order is saved.</summary>
    public int? CommentLeadId { get; set; }
    public int? CommentLeadNumber { get; set; }
    /// <summary>The seller already answered "same customer or a new one?" for this draft, so it is not asked again.</summary>
    public bool CustomerMatchResolved { get; set; }
}

/// <summary>"order for Sara": the next idle message is read as that saved customer's items instead of asking for name and phone again.</summary>
public sealed class OrderForCustomerData
{
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>A "did you mean ...?" tap offered for an unknown product name: the button title as sent, and the catalog label it stands for.</summary>
public sealed class ProductSuggestion
{
    public string Title { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>Everything an in-flight multi-turn flow needs, serialized to ConversationSession.ContextJson.</summary>
public sealed class SessionContextData
{
    public OrderForCustomerData? OrderForCustomer { get; set; }
    /// <summary>The did-you-mean buttons last sent for the product question (<c>ProductChoice</c>); a tap on one is read as "2 &lt;label&gt;".</summary>
    public List<ProductSuggestion>? ProductSuggestions { get; set; }
    public PendingOrderData? PendingOrder { get; set; }
    /// <summary>When the draft was first shown for confirmation (UTC). Set by the engine on persist; cleared with the draft.</summary>
    public DateTime? PendingOrderAskedAt { get; set; }
    public string? PendingMissingField { get; set; }
    public List<string>? ClarificationOptions { get; set; }
    /// <summary>The sentence the bot just asked "what did you mean?" about; when the seller picks an option it is learned for this seller.</summary>
    public string? ClarifiedText { get; set; }
    /// <summary>Phrases this seller taught (normalised sentence -> command text), see ConversationEngine.Learning.cs.</summary>
    public Dictionary<string, string>? LearnedPhrases { get; set; }
    /// <summary>How many times the seller confirmed each learned phrase; a phrase is used only from the second confirmation (a stray tap teaches nothing).</summary>
    public Dictionary<string, int>? LearnedVotes { get; set; }
    public string? PendingNewProductName { get; set; }
    /// <summary>Products scanned with a barcode scanner and not yet turned into an order (product id -> pieces); "scan done" makes the order draft.</summary>
    public Dictionary<int, int>? ScanCart { get; set; }
    /// <summary>Saved customer whose phone matches the draft's but whose name differs ("Muhammad Ali" for "Ali"); asked about before saving.</summary>
    public string? PendingCustomerMatchName { get; set; }
    /// <summary>Products the seller named ("naye products hain") that the bot is still waiting to get a price for.</summary>
    public List<string>? PendingPriceProducts { get; set; }
    /// <summary>When <see cref="PendingPriceProducts"/> was last set; an old question is dropped, so a number sent later is not taken as a price.</summary>
    public DateTime? PendingPriceSetAt { get; set; }
    /// <summary>Cost/stock/attributes already given for those products, kept until their price arrives (product name -> details).</summary>
    public Dictionary<string, ProductExtras>? PendingProductExtras { get; set; }
    /// <summary>A product line waiting for the seller's YES (JSON of the ProductLine); null when nothing waits.</summary>
    public string? PendingProductJson { get; set; }
    public List<int>? BulkStatusOrderIds { get; set; }
    public int? CancelOrderId { get; set; }
    public int? CodCollectedOrderId { get; set; }
    public string? RuntimeFilterCommand { get; set; }
    public int? TrackingPromptOrderId { get; set; }
    public string? BroadcastMessageText { get; set; }
    public string? BroadcastChannel { get; set; }
    public string? PendingDiscountCode { get; set; }
    /// <summary>Order ids in the order of the last numbered list shown, so "mark 1 shipped" means list position 1.</summary>
    /// <summary>Days the seller's supplier takes to deliver stock ("maal aane mein 5 din lagte hain"); the re-order advice compares days of stock left with it. Null = not told.</summary>
    public int? SupplierLeadDays { get; set; }
    /// <summary>The seller-local day (yyyy-MM-dd) the "add cost prices" reminder was last sent, so it comes at most once a day.</summary>
    public string? SetupNudgeDay { get; set; }
    /// <summary>True once the "4 ways to add products" tips and the units hint were shown, so later saves only confirm the product.</summary>
    public bool ProductHowToShown { get; set; }
    /// <summary>The page (1-based) and order ("az" / "latest" / null = as added) of the catalog list the seller last looked at ("catalog next").</summary>
    public int CatalogPage { get; set; }
    public string? CatalogSort { get; set; }
    /// <summary>What the seller searched for in the catalog ("catalog search chini"); the list shows only matches until "catalog reset".</summary>
    public string? CatalogQuery { get; set; }
    /// <summary>How many products a catalog page holds (5-50); 0 = 10. Kept until the seller changes it ("catalog size 25").</summary>
    public int CatalogPageSize { get; set; }
    /// <summary>The seller turned the quick-action buttons off ("shortcut off").</summary>
    public bool ShortcutsOff { get; set; }
    /// <summary>The one-time explanation of the quick-action buttons has been shown.</summary>
    public bool ShortcutIntroShown { get; set; }

    /// <summary>WhatsApp media id of the uploaded import file, re-downloaded on YES so the parsed rows needn't be stored.</summary>
    public string? PendingImportMediaId { get; set; }
    /// <summary>Its file name, so the re-read on YES labels a CSV sheet the same way the check did.</summary>
    public string? PendingImportFileName { get; set; }
    /// <summary>The state the upload arrived in (a mid-onboarding seller goes back there after YES/NO).</summary>
    public ConversationState? ImportReturnState { get; set; }

    public List<int>? LastListOrderIds { get; set; }
    public List<int>? LastListCustomerIds { get; set; }
    public int CustomerListPage { get; set; }
    public List<PendingOrderItemData>? QueuedSeparateOrderItems { get; set; }
    public PendingOrderData? QueuedOrderTemplate { get; set; }
    /// <summary>Orders for other customers from the same message, walked one by one after the current draft.</summary>
    public List<PendingOrderData>? QueuedOrders { get; set; }
    /// <summary>Complete drafts for several customers awaiting one "YES" (screen 22b).</summary>
    public List<PendingOrderData>? MultiOrders { get; set; }
    public List<int>? ReceiptCandidateOrderIds { get; set; }
    public decimal? ReceiptAmount { get; set; }
    public string? ReceiptProvider { get; set; }
    public string? ReceiptTransactionId { get; set; }
    public int? DeleteCustomerId { get; set; }
    public int? LoyaltyOrderId { get; set; }
    /// <summary>The saved order being edited ("edit order 12"), and whether its pre-edit snapshot is already in the undo log.</summary>
    public int? EditOrderId { get; set; }
    public bool EditSnapshotLogged { get; set; }
    /// <summary>Set after a bare "nahi" in edit mode: the next word decides between editing on ("jaari") and discarding the edits ("chhoro").</summary>
    public bool EditDiscardPending { get; set; }
    /// <summary>The newest "order edited" undo entry before this edit started: a newer one belongs to this edit and is the only one "chhoro" may undo.</summary>
    public int EditBaselineLogId { get; set; }
    public decimal? LoyaltyDiscountPercent { get; set; }
    /// <summary>Typed steps read from a voice note that are waiting for YES (risky actions), in the order they will run.</summary>
    public List<string>? PendingVoiceSteps { get; set; }
    /// <summary>When <see cref="PendingVoiceSteps"/> were parked (UTC). A YES after the window is not approval of those steps.</summary>
    public DateTime? PendingVoiceParkedAt { get; set; }
    /// <summary>When "reset account" asked for YES (UTC). A YES after the window does not wipe the account.</summary>
    public DateTime? ResetAskedAt { get; set; }
    /// <summary>Id of the open reset prompt. Created with the prompt, cleared when it is answered. A YES needs it to claim the reset.</summary>
    public string? ResetGenerationId { get; set; }
    /// <summary>Newest action-log id for the seller when the steps were parked. Any change since then (a new order, edit, undo...) makes the YES stale.</summary>
    public int PendingVoiceLogId { get; set; }
    /// <summary>Voice steps after an order draft; they run once that draft is saved, edited or cancelled (one draft at a time).</summary>
    public List<string>? PendingVoiceContinue { get; set; }
    public string? SelectedPlan { get; set; }
    /// <summary>Support query whose drafted reply is awaiting YES/EDIT.</summary>
    public int? SupportQueryId { get; set; }
    /// <summary>Set by "lead N converted": the next order logged is linked to this comment lead.</summary>
    public int? ConvertingLeadId { get; set; }

    /// <summary>Interactive guide (screens 1c-2/1c-3): topic, and the step shown last (0 = topic not chosen yet).</summary>
    public string? GuideTopic { get; set; }
    public int GuideStep { get; set; }
    /// <summary>Set when "guide" is opened mid-onboarding, so exiting resumes there instead of dropping to Idle
    /// (an incomplete seller landing on Idle would otherwise restart onboarding from scratch).</summary>
    public ConversationState? GuideReturnState { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static SessionContextData FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new SessionContextData()
            : JsonSerializer.Deserialize<SessionContextData>(json) ?? new SessionContextData();
}
