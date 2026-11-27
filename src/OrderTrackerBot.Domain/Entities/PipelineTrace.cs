namespace OrderTrackerBot.Domain.Entities;

/// <summary>
/// Stage 9. One row per handled message: what it did, how long it took, and how it ended. Counts, durations, the action kind and the outcome
/// only. No message text and no phone number, so the table can be read without exposing a seller's words.
/// </summary>
public class PipelineTrace
{
    public int Id { get; set; }
    /// <summary>Random id for the message (a GUID in "N" form). Joins this row to the logs written for the same message.</summary>
    public string TraceId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int TotalMs { get; set; }
    /// <summary>AI calls on the message route (router, turn interpreter, message analysis).</summary>
    public int AiCalls { get; set; }
    public int AiMs { get; set; }
    /// <summary>How many questions the bot asked the seller back on this message.</summary>
    public int ClarificationCount { get; set; }
    /// <summary>The action kind that was chosen (a command kind or a model action), when one was.</summary>
    public string? SelectedAction { get; set; }
    /// <summary>The last pipeline stage reached (see ConversationPipelineStage).</summary>
    public string? Stage { get; set; }
    /// <summary>Executed | Refused | Clarified | Replied | Failed.</summary>
    public string Outcome { get; set; } = "";
    /// <summary>The exception type when the message failed. Never the exception message, which can carry data.</summary>
    public string? FailureReason { get; set; }
}
