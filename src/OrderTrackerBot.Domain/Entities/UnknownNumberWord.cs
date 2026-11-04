namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// A number word a voice note used that the bot could not read, with how often it came up. The word only: no transcript, no seller.
/// Kept so the spelling list (<c>SpokenNumbers</c>) can grow from real use across restarts.
/// </summary>
public class UnknownNumberWord
{
    public int Id { get; set; }
    public required string Word { get; set; }
    public int Count { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
