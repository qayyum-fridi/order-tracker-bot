namespace OrderTrackerBot.Domain.Entities;

/// <summary>A customer's photo (one per customer), stored in the database like the product photos.</summary>
public class CustomerPhoto
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public int CustomerId { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
