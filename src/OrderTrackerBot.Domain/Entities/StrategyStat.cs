namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// Outcome counts of one bot decision per seller (see <c>StrategyPolicy</c>). Context "price_guard" holds one row per answer
/// ("written", "catalog", "update") and one "*" row counting every decision in that context.
/// <see cref="Picked"/> = times the answer was the final choice (asked or applied); <see cref="Overridden"/> = times the seller
/// corrected an applied choice (draft edited or cancelled, or the order undone).
/// </summary>
public class StrategyStat
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public required string Context { get; set; }
    public required string Arm { get; set; }
    public int Picked { get; set; }
    public int Overridden { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
