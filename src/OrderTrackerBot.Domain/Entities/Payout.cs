namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// Money a courier or payment gateway paid out to the seller for COD / online orders ("payout 11700 TCS orders 1 2 3 fee 300").
/// Orders linked to it are settled (<see cref="Order.PayoutId"/>) and marked paid; <see cref="UndoJson"/> keeps what each order looked like
/// before so "payout undo" can restore it.
/// </summary>
public class Payout
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    /// <summary>Who paid: "TCS", "Leopards", "JazzCash"... (free text, as the seller wrote it).</summary>
    public required string Source { get; set; }
    /// <summary>The amount that actually arrived.</summary>
    public decimal Amount { get; set; }
    /// <summary>Charges the courier / gateway kept (already out of <see cref="Amount"/>); recorded so the difference to the orders' total is explained.</summary>
    public decimal Fee { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string? UndoJson { get; set; }
}
