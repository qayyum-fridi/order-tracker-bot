namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// A seller's catalog category, one level of sub-categories (<see cref="ParentId"/>). Products keep the text
/// "Parent &gt; Child" in Product.Category, so catalog and reports read it as before.
/// </summary>
public class Category
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public Seller? Seller { get; set; }
    public required string Name { get; set; }
    public int? ParentId { get; set; }
    public Category? Parent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
