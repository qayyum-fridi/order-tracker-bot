namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// A person who works in the shop, kept as a record only: no login and no permissions (anyone with the owner's phone can
/// still use every command). Removing one switches the record off (<see cref="IsActive"/>) so past wages still point to a person.
/// </summary>
public class StaffMember
{
    public int Id { get; set; }
    public int SellerId { get; set; }
    public required string Name { get; set; }
    public string? Phone { get; set; }
    /// <summary>Free text, e.g. "munshi", "driver", "helper".</summary>
    public string? Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
