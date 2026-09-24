using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Domain.Entities;

public class Order
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Unpaid;
    public OrderPaymentMethod PaymentMethod { get; set; } = OrderPaymentMethod.Cod;
    public DateTime? PaidAt { get; set; }

    public string? DiscountCode { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal Total { get; set; }

    public string? TrackingCourier { get; set; }
    public string? TrackingNumber { get; set; }

    public DateTime? DeliveryDate { get; set; }
    public string? OrderSource { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public int? ProductId { get; set; }
    public required string ProductNameSnapshot { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; } = 1;

    public decimal LineTotal => UnitPrice * Quantity;
}

public class Discount
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    public required string Code { get; set; }
    public DiscountType Type { get; set; }
    public decimal Value { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class LoyaltyRule
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    public int OrderThreshold { get; set; }
    public decimal DiscountPercent { get; set; }
    public bool IsActive { get; set; } = true;
}

public class SellerPaymentMethod
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    public SellerPaymentMethodType Type { get; set; }
    public required string AccountNumberOrId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Log of mutating actions, replayed in reverse by the "undo" command.</summary>
public class ActionLog
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public ActionType ActionType { get; set; }
    public int? OrderId { get; set; }
    /// <summary>JSON snapshot of the fields changed, enough to revert them.</summary>
    public required string PayloadJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Undone { get; set; }
}

/// <summary>Merchant complaining/praising the bot itself — routed to the founder, not the buyer.</summary>
public class MerchantFeedback
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public required string Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Buyer sentiment the merchant chooses to log against an order (V3+, kept for forward-compat).</summary>
public class CustomerFeedback
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public int? OrderId { get; set; }
    public string? CustomerName { get; set; }
    public required string Text { get; set; }
    public string? Sentiment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A promotion sent to past customers (V5). Delivery needs a Meta-approved template / SMS provider.</summary>
public class Campaign
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public required string Channel { get; set; } // whatsapp | sms | both
    public required string Message { get; set; }
    public required string TargetFilter { get; set; } // all | repeat_customers | inactive_30d
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<CampaignSend> Sends { get; set; } = new List<CampaignSend>();
}

public class CampaignSend
{
    public int Id { get; set; }
    public int CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    public int? CustomerId { get; set; }
    public required string CustomerPhone { get; set; }
    public required string Channel { get; set; }
    public required string Status { get; set; } // sent | failed | not_configured
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Every inbound/outbound message, for debugging AI parsing failures after launch.</summary>
public class MessageLog
{
    public int Id { get; set; }
    public required string Phone { get; set; }
    public required string Direction { get; set; } // inbound | outbound
    public required string RawText { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A seller's Instagram Business/Creator account connected via OAuth, so comment webhooks can be routed to them (V5, section 6c).</summary>
public class InstagramConnection
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    /// <summary>The IG professional account id — the <c>entry.id</c> on comment webhooks.</summary>
    public required string IgUserId { get; set; }
    public string? Username { get; set; }
    /// <summary>Facebook Page linked to the IG account (Facebook Login mode only).</summary>
    public string? PageId { get; set; }
    public required string AccessToken { get; set; }
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Null when the token never expires (Page tokens).</summary>
    public DateTime? TokenExpiresAt { get; set; }
}

/// <summary>An Instagram comment on the seller's post, captured by webhook before (maybe) becoming an order.</summary>
public class CommentLead
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    /// <summary>Per-seller running number shown in chat ("lead 1 converted").</summary>
    public int Number { get; set; }
    /// <summary>Meta's comment id; unique so a redelivered webhook is ignored.</summary>
    public required string IgCommentId { get; set; }
    public string? CommenterUsername { get; set; }
    public required string CommentText { get; set; }
    public string? PostId { get; set; }
    public required string ClassifiedIntent { get; set; } // order_interest | support_query | spam | unclear
    public string Status { get; set; } = "new"; // new | followed_up | converting | converted_to_order | dismissed
    public int? OrderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A buyer question the seller forwarded (or an IG comment question); the bot drafts a reply, the seller sends it.</summary>
public class SupportQuery
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public int Number { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public required string Source { get; set; } // whatsapp | instagram_comment
    public required string QueryText { get; set; }
    public string? SuggestedReply { get; set; }
    public string Status { get; set; } = "open"; // open | replied | resolved
    public int? LinkedOrderId { get; set; }
    /// <summary>Set for instagram_comment queries: the comment the reply is posted under.</summary>
    public string? IgCommentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
