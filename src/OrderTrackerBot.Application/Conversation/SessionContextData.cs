using System.Text.Json;

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
    public decimal Subtotal { get; set; }
    public decimal Total { get; set; }
    public int? DuplicateOfOrderId { get; set; }
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
    public decimal? LoyaltyDiscountPercent { get; set; }
    public string? SelectedPlan { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static SessionContextData FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new SessionContextData()
            : JsonSerializer.Deserialize<SessionContextData>(json) ?? new SessionContextData();
}
