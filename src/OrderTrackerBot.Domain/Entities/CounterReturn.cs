namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// A product a buyer brings back to the counter, with no order to find ("return Shirt 1 cash"). It is not an order row, so no order
/// status or total changes. Stock is added back at once; a cash refund with an open drawer also writes a cash-out movement.
/// </summary>
public class CounterReturn
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public int? ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
    /// <summary>Refund amount: the catalog price x quantity.</summary>
    public decimal Amount { get; set; }
    /// <summary>cash | bank | none (none = exchange or store credit, no money leaves).</summary>
    public string RefundMethod { get; set; } = "none";
    public int? CashMovementId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
