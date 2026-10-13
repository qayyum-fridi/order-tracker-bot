namespace OrderTrackerBot.Domain.Entities;

public enum CashMovementKind
{
    /// <summary>Money put in (change float, a payment received outside an order).</summary>
    In = 0,
    /// <summary>Money taken out (a purchase, a withdrawal).</summary>
    Out = 1,
    /// <summary>A counter sale taken in cash without an order in the bot.</summary>
    Sale = 2
}

/// <summary>
/// One shift of the cash drawer: opened with a float, closed with the cash the seller counted. Expected cash is worked out when asked
/// (opening + COD orders marked paid since it opened + cash in/sales - cash out) and stored at close with the counted amount, so a
/// later change to an order never rewrites a closed shift.
/// </summary>
public class CashDrawerSession
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public decimal OpeningAmount { get; set; }
    public DateTime? ClosedAt { get; set; }
    public decimal? CountedAmount { get; set; }
    public decimal? ExpectedAmount { get; set; }
    public string? Note { get; set; }
    public ICollection<CashMovement> Movements { get; set; } = new List<CashMovement>();
}

public class CashMovement
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public int SessionId { get; set; }
    public CashDrawerSession? Session { get; set; }
    public CashMovementKind Kind { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
