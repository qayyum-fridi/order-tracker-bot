namespace OrderTrackerBot.Domain.Entities;

public enum CustomFieldEntity
{
    Product = 1,
    Customer = 2,
    Order = 3
}

public enum CustomFieldType
{
    Text = 0,
    Number = 1,
    Date = 2
}

/// <summary>A seller-defined attribute ("Fabric" on products, "Birthday" on customers) — values are free text.</summary>
public class CustomField
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public CustomFieldEntity Entity { get; set; }
    public required string Name { get; set; }
    /// <summary>Allowed values joined with '|' (null = free text). Choice fields are set by tapping a button/list row.</summary>
    public string? Options { get; set; }
    /// <summary>Text | Number | Date. Ignored for choice fields (<see cref="Options"/> set).</summary>
    public CustomFieldType Type { get; set; }
    /// <summary>Internal field (e.g. "Cost"): shown to the seller only — never on receipts or the shareable catalog.</summary>
    public bool IsPrivate { get; set; }
    /// <summary>Soft delete: "remove field" hides the field and its values (global query filter) so "undo" can bring them back.</summary>
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<string> OptionList => string.IsNullOrEmpty(Options) ? new List<string>() : Options.Split('|').ToList();
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
