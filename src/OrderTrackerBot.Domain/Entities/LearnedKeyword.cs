namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// A word that the AI router mapped to an intent in a message the catalog's own keywords did not cover. Shared by all sellers
/// (no seller id): a word counts once per message, and only words with enough votes are used as hints. Words only: no message text,
/// no seller, no names (names and product words are filtered out before a word is kept).
/// </summary>
public class LearnedKeyword
{
    public int Id { get; set; }
    public required string Word { get; set; }
    public required string IntentId { get; set; }
    public int Votes { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
