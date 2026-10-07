namespace OrderTrackerBot.Domain.Entities;

public enum CustomFieldEntity
{
    Product = 1,
    Customer = 2,
    Order = 3
}

/// <summary>A seller-defined attribute ("Fabric" on products, "Birthday" on customers) — values are free text.</summary>
public class CustomField
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public CustomFieldEntity Entity { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The value of one field for one product/customer/order (<see cref="EntityId"/> points at the entity named by the field).</summary>
public class CustomFieldValue
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public int CustomFieldId { get; set; }
    public CustomField? CustomField { get; set; }
    public int EntityId { get; set; }
    public required string Value { get; set; }
}
