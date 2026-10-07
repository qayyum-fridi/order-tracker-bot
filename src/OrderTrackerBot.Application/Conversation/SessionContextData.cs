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
}

public sealed class PendingOrderData
{
    public string? CustomerName { get; set; }
    public List<PendingOrderItemData> Items { get; set; } = new();
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? PaymentMethodText { get; set; }
    public string? DiscountCode { get; set; }
    public string? OrderSource { get; set; }
    public bool FromScreenshot { get; set; }
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
}

/// <summary>Everything an in-flight multi-turn flow needs, serialized to ConversationSession.ContextJson.</summary>
public sealed class SessionContextData
{
    public PendingOrderData? PendingOrder { get; set; }
    public string? PendingMissingField { get; set; }
    public List<string>? ClarificationOptions { get; set; }
    public string? PendingNewProductName { get; set; }
    public List<int>? BulkStatusOrderIds { get; set; }
    public int? CancelOrderId { get; set; }
    public int? CodCollectedOrderId { get; set; }
    public string? RuntimeFilterCommand { get; set; }
    public int? TrackingPromptOrderId { get; set; }
    public string? BroadcastMessageText { get; set; }
    public string? BroadcastChannel { get; set; }
    public string? PendingDiscountCode { get; set; }
    /// <summary>Order ids in the order of the last numbered list shown, so "mark 1 shipped" means list position 1.</summary>
    /// <summary>The seller turned the quick-action buttons off ("shortcut off").</summary>
    public bool ShortcutsOff { get; set; }
    /// <summary>The one-time explanation of the quick-action buttons has been shown.</summary>
    public bool ShortcutIntroShown { get; set; }

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
    public decimal? LoyaltyDiscountPercent { get; set; }
    /// <summary>Typed steps read from a voice note that are waiting for YES (risky actions), in the order they will run.</summary>
    public List<string>? PendingVoiceSteps { get; set; }
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
