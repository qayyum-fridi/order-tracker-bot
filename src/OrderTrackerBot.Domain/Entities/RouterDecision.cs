namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// What the AI intent router returned and what the bot did with it, kept so the confidence bars (run at 0.7, ask at 0.4) can be tuned from real use.
/// <see cref="Outcome"/> is <c>run</c> (confident, ran), <c>guess</c> (asked "do you mean"), <c>ignored</c> (too unsure), or <c>guess_yes</c> /
/// <c>guess_no</c> (the seller's answer to a guess). Intent and number only: no message text, no seller.
/// </summary>
public class RouterDecision
{
    public int Id { get; set; }
    public required string IntentId { get; set; }
    public double Confidence { get; set; }
    public required string Outcome { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
