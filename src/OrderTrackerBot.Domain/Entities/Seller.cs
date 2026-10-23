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
    /// <summary>The seller's own email address: receipts are emailed here by default ("email ali@shop.com"). Cleared by "reset account".</summary>
    public string? Email { get; set; }
    /// <summary>Receipt paper: null/"A5" (default), "A4", "80mm" or "58mm" thermal roll ("receipt size 80mm").</summary>
    public string? ReceiptSize { get; set; }
    /// <summary>Line printed at the bottom of every receipt instead of the default thank-you ("receipt footer ...").</summary>
    public string? ReceiptFooter { get; set; }
    /// <summary>The seller's own receipt look (colour, title, layout, what to show) as JSON; null = standard look ("receipt design").</summary>
    public string? ReceiptDesignJson { get; set; }
    /// <summary>Short line printed under the shop name on every receipt ("receipt header ...").</summary>
    public string? ReceiptHeader { get; set; }
    /// <summary>Contact line printed under the shop name, e.g. email and extra phone numbers ("receipt contact ...").</summary>
    public string? ReceiptContact { get; set; }
    /// <summary>Label sheet for shipping / business labels: null/"4x6" (default), "A6" or "50x30" (mm sticker) ("label size a6").</summary>
    public string? LabelSize { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Free trial end; null until onboarding completes (trial starts then).</summary>
    public DateTime? TrialEndsAt { get; set; }
    public SubscriptionPlan Plan { get; set; } = SubscriptionPlan.Trial;
    public SellerStatus Status { get; set; } = SellerStatus.Active;
    public DateTime? SubscriptionActiveUntil { get; set; }
    public DateTime? TrialReminderSentAt { get; set; }
    public DateTime? LastWeeklySummaryAt { get; set; }
    /// <summary>SHA-256 of the client dashboard bearer token (null = none issued). The token itself is never stored.</summary>
    public string? ClientApiTokenHash { get; set; }
    /// <summary>Salted SHA-256 of the WhatsApp login code waiting for this seller (null = none). Cleared on use.</summary>
    public string? ClientLoginCodeHash { get; set; }
    public DateTime? ClientLoginCodeSentAt { get; set; }
    public DateTime? ClientLoginCodeExpiresAt { get; set; }
    /// <summary>Wrong codes typed against the current login code. The code stops working after five.</summary>
    public int ClientLoginAttempts { get; set; }

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
    /// <summary>Price for buyers in price group A / B / C; null = the regular <see cref="Price"/>.</summary>
    public decimal? PriceA { get; set; }
    public decimal? PriceB { get; set; }
    public decimal? PriceC { get; set; }
    /// <summary>Price group (A, B or C) a new sale uses for this product; null = the regular price.</summary>
    public string? DefaultPriceGroup { get; set; }
    /// <summary>sale | service. null = sale.</summary>
    public string? ItemKind { get; set; }
    /// <summary>The price a buyer in this group pays: the group's level when set, else <see cref="Price"/>.</summary>
    public decimal PriceFor(string? group) => group switch
    {
        "A" => PriceA ?? Price,
        "B" => PriceB ?? Price,
        "C" => PriceC ?? Price,
        _ => Price
    };
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
    /// <summary>The buyer's own email, for "email receipt 12 to buyer". Saved with "Sara ka email sara@mail.com".</summary>
    public string? Email { get; set; }
    public string? PreferredContact { get; set; }
    public string? Notes { get; set; }
    /// <summary>Which price group (A, B or C) this buyer pays; null = regular price.</summary>
    public string? PriceGroup { get; set; }

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
