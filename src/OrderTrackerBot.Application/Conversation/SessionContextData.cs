using System.Text.Json;

namespace OrderTrackerBot.Application.Conversation;

public sealed class PendingOrderItemData
{
    public required string ProductName { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
}

public sealed class PendingOrderData
{
    public string? CustomerName { get; set; }
    public List<PendingOrderItemData> Items { get; set; } = new();
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? PaymentMethodText { get; set; }
    public string? DiscountCode { get; set; }
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
    public string? PendingDiscountCode { get; set; }
    public List<PendingOrderItemData>? QueuedSeparateOrderItems { get; set; }
    public PendingOrderData? QueuedOrderTemplate { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static SessionContextData FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new SessionContextData()
            : JsonSerializer.Deserialize<SessionContextData>(json) ?? new SessionContextData();
}
