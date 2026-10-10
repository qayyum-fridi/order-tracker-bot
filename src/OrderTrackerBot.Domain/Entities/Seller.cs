using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Domain.Entities;

/// <summary>The merchant — the only party with a WhatsApp chat with the bot.</summary>
public class Seller
{
    public int Id { get; set; }
    public required string WhatsAppPhoneNumber { get; set; }
    public string? BusinessName { get; set; }
    public bool OnboardingComplete { get; set; }
    public string PreferredLanguage { get; set; } = "roman_urdu";
    public string? City { get; set; }
    /// <summary>IANA zone used for "today" boundaries; all stored timestamps stay UTC.</summary>
    public string TimeZoneId { get; set; } = "Asia/Karachi";
    public string? BusinessType { get; set; }
    public string? InstagramHandle { get; set; }
    /// <summary>Delivery charge added to every new order unless changed on that order ("delivery 200" sets it, "free delivery" clears it).</summary>
    public decimal DefaultDeliveryCharge { get; set; }
    /// <summary>National Tax Number printed on receipts (optional; online sellers need one to use couriers/marketplaces).</summary>
    public string? Ntn { get; set; }
    /// <summary>Sales Tax Registration Number; only sales-tax-registered sellers may issue a tax invoice.</summary>
    public string? Strn { get; set; }
    /// <summary>Percent of sales tax included in the seller's prices (e.g. 18). 0 = not charging sales tax, receipts stay plain receipts.</summary>
    public decimal SalesTaxRate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Free trial end; null until onboarding completes (trial starts then).</summary>
    public DateTime? TrialEndsAt { get; set; }
    public SubscriptionPlan Plan { get; set; } = SubscriptionPlan.Trial;
    public DateTime? SubscriptionActiveUntil { get; set; }
    public DateTime? TrialReminderSentAt { get; set; }
    public DateTime? LastWeeklySummaryAt { get; set; }

    public ConversationSession? Session { get; set; }
    public ICollection<Product> Products { get; set; } = new List<Product>();
    public ICollection<Customer> Customers { get; set; } = new List<Customer>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<Discount> Discounts { get; set; } = new List<Discount>();
    public ICollection<LoyaltyRule> LoyaltyRules { get; set; } = new List<LoyaltyRule>();
    public ICollection<SellerPaymentMethod> PaymentMethods { get; set; } = new List<SellerPaymentMethod>();
}

/// <summary>Persists where a seller is mid-flow so the bot can resume after a days-long gap.</summary>
public class ConversationSession
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    public ConversationState State { get; set; } = ConversationState.Idle;
    /// <summary>JSON-serialized scratch data for the in-flight flow (draft order, clarification options, pending product name...).</summary>
    public string? ContextJson { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Product
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    /// <summary>piece | kg | gram | dozen | liter | meter | yard | pack — the catalog listing is the atomic sellable unit.</summary>
    public string UnitType { get; set; } = "piece";
    /// <summary>How many units one listing holds, e.g. 5 for a "Sugar 5kg" pack.</summary>
    public decimal UnitQty { get; set; } = 1;
    public string? Category { get; set; }
    public string? Size { get; set; }
    public string? Color { get; set; }
    public string? Sku { get; set; }
    public int? StockQty { get; set; }
    /// <summary>What the seller paid for one listing (seller-only: never shown to buyers or on receipts).</summary>
    public decimal? CostPrice { get; set; }
    /// <summary>Seller-defined extra attributes ("fabric": "cotton") as a JSON object of name -> value; Color/Size/Category/Sku have their own columns.</summary>
    public string? AttributesJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The seller's logo/banner for PDF receipts. Its own table so the image bytes aren't loaded with the Seller on every message.</summary>
public class SellerBranding
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public byte[]? Logo { get; set; }
    public byte[]? Banner { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A business cost the seller logged ("expense 500 packaging"). Category is the first word of the note, lower-cased, so the monthly net can group them.</summary>
public class Expense
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public decimal Amount { get; set; }
    public required string Category { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Stock written off (damaged, expired, stolen). UnitCost is copied from the product when logged, so later cost changes don't rewrite history.</summary>
public class Loss
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public int? ProductId { get; set; }
    public required string ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Pay given to a worker ("Ali ki 5000 dihari"). Amount is the total paid; Days is set for daily-rate entries.</summary>
public class WageEntry
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public required string WorkerName { get; set; }
    public decimal Amount { get; set; }
    public int? Days { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Customer
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    public required string Name { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? PreferredContact { get; set; }
    public string? Notes { get; set; }

    /// <summary>Soft delete: hidden from customer views, restorable for 30 days, then purged.</summary>
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}

/// <summary>Wholesale/bulk pricing: the per-unit rate for orders of at least <see cref="MinQty"/> units.</summary>
public class PriceTier
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public decimal MinQty { get; set; }
    public decimal PricePerUnit { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
