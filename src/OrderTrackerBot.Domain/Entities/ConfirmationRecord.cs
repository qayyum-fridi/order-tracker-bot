namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// Stage 7. One YES/NO question for one write, bound to a seller, a user, the target record's version and an expiry. A YES is accepted only
/// for the record it was asked under, and only once (<see cref="ConsumedAt"/> is set by one atomic update).
/// </summary>
public class ConfirmationRecord
{
    /// <summary>Random id (a GUID in "N" form), created when the question is asked.</summary>
    public string Id { get; set; } = "";
    public int SellerId { get; set; }
    /// <summary>The user the question was asked to (the seller's WhatsApp number).</summary>
    public string UserId { get; set; } = "";
    /// <summary>The command the seller is confirming (e.g. "cancel_order", "update_status").</summary>
    public string Action { get; set; } = "";
    /// <summary>The order the write changes, when it changes one.</summary>
    public int? OrderId { get; set; }
    /// <summary>The value the write sets (e.g. the new status). Null when the action has none.</summary>
    public string? Value { get; set; }
    /// <summary>The target's version when the question was asked (see SafetyGateway.VersionOf). A change since then makes the YES stale.</summary>
    public string RecordVersion { get; set; } = "";
    /// <summary>Pending | Consumed | Rejected | Expired | Cancelled | Stale.</summary>
    public string Status { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}
