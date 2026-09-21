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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

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
    public bool IsActive { get; set; } = true;
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

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
