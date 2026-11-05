namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// One seller's vote for a word of a learned intent: a seller counts once per word and intent, so a single seller cannot make a word a shared
/// hint on their own. The seller id is an internal number; no message text is kept.
/// </summary>
public class LearnedKeywordVote
{
    public int Id { get; set; }
    public required string Word { get; set; }
    public required string IntentId { get; set; }
    public int SellerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
