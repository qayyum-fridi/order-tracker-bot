namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// Stage 6 persisted form of a clarification question. Lists and maps are stored as JSON text, so the table needs no child tables.
/// Expand-only: nothing reads or writes this table until the clarification store is wired in.
/// </summary>
public class PendingActionRecord
{
    /// <summary>Random id (a GUID in "N" form), created when the question is asked.</summary>
    public string Id { get; set; } = "";
    public int SellerId { get; set; }
    public string UserId { get; set; } = "";
    public string Action { get; set; } = "";
    /// <summary>Field name to resolved record id, as JSON.</summary>
    public string ResolvedReferencesJson { get; set; } = "{}";
    /// <summary>Fields still needed, in order, as JSON.</summary>
    public string MissingFieldsJson { get; set; } = "[]";
    /// <summary>Field name to the value the seller gave, as JSON.</summary>
    public string AnswersJson { get; set; } = "{}";
    public string CurrentQuestion { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>The original command (the parsed action, as JSON). Empty when none was stored.</summary>
    public string ActionJson { get; set; } = "";
    /// <summary>Replies that did not fit the field being asked. The question is dropped after the limit.</summary>
    public int Attempts { get; set; }
    /// <summary>Waiting | Completed | Expired | Cancelled.</summary>
    public string Status { get; set; } = "Waiting";
}
