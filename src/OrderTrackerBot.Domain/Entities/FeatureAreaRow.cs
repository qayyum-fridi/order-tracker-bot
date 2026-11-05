namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// A feature area changeable at runtime: when a message matches <see cref="Pattern"/>, the seller is offered the typed commands in
/// <see cref="Options"/> (a JSON array) under <see cref="Question"/>. Seeded once from <c>Intents/feature-areas.seed.json</c>, keyed by its
/// question; areas are tried in the order they were added. Words only: no seller, no message text.
/// </summary>
public class FeatureAreaRow
{
    public int Id { get; set; }
    public required string Question { get; set; }
    public required string Pattern { get; set; }
    public required string Options { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
