namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// A question a buyer asked, as the seller wrote it down ("enquiry Kurti blue"). The bot never sees buyers' chats, so this only holds
/// what the seller logged. <see cref="ProductId"/> is set when the text names a catalog product.
/// </summary>
public class BuyerEnquiry
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public int? ProductId { get; set; }
    public required string Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
