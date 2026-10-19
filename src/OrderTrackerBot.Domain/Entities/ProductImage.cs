namespace OrderTrackerBot.Domain.Entities;

/// <summary>A photo of a product, stored in the database like the receipt logo. Gallery order is the id order.</summary>
public class ProductImage
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
