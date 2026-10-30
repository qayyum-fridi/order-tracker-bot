namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// A message the bot could not place, stored masked (names, phones and amounts removed) for the learning report.
/// <see cref="SellerHash"/> is a keyed hash of the seller id, never the phone number. Rows are kept for a short time only.
/// </summary>
public class UnplacedUtterance
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public required string SellerHash { get; set; }
    public required string MaskedText { get; set; }
    /// <summary>Where it was missed, e.g. freeform_unclear, voice_reject, voice_clarify.</summary>
    public required string Stage { get; set; }
    public string? RouterIntent { get; set; }
    public double? RouterConfidence { get; set; }
}
