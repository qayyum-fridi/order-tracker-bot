namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// A word the bot matches on, changeable at runtime without a build. <see cref="IntentId"/> set: a keyword of that intent.
/// <see cref="IntentId"/> null: a stop word (ignored when reading a message). Seeded once from <c>Intents/keywords.seed.json</c>
/// and <c>intents.json</c>; after that, edits in this table are the source of truth. Word-only: no message text, no seller.
/// </summary>
public class KeywordRule
{
    public int Id { get; set; }
    public required string Word { get; set; }
    public string? IntentId { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
