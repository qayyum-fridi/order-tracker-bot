using System.Diagnostics;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Stage 9. What one message did while it was handled: AI calls and time, questions asked back, the chosen action, the last stage and the
/// failure type. The engine fills it in; the worker writes it as one <see cref="PipelineTrace"/> row. It never holds message text or phone numbers.
/// </summary>
public sealed class ConversationTrace
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private string? _selectedAction;
    private string? _stage;
    private string? _failureReason;

    public string TraceId { get; } = Guid.NewGuid().ToString("N");
    public int AiCalls { get; private set; }
    public int AiMs { get; private set; }
    public int ClarificationCount { get; private set; }

    /// <summary>Stage 9 phase 1 (voice): the planned steps and the conflicts ActionPlanner found. Action names, entity keys and effects only.</summary>
    public IReadOnlyList<PlannedStep> PlannedSteps { get; private set; } = Array.Empty<PlannedStep>();
    public IReadOnlyList<PlanConflict> DetectedConflicts { get; private set; } = Array.Empty<PlanConflict>();

    public void RecordPlan(IReadOnlyList<PlannedStep> steps, IReadOnlyList<PlanConflict> conflicts)
    {
        PlannedSteps = steps;
        DetectedConflicts = conflicts;
    }

    /// <summary>Runs one AI call on the message route and records its count and time.</summary>
    public async Task<T> AiAsync<T>(Func<Task<T>> call)
    {
        AiCalls++;
        var started = Stopwatch.GetTimestamp();
        try { return await call(); }
        finally { AiMs += (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    public void AskedBack() => ClarificationCount++;

    /// <summary>The first action chosen wins (a typed command is chosen before any model action).</summary>
    public void SelectAction(string action) => _selectedAction ??= action;

    public void ReachedStage(ConversationPipelineStage stage) => _stage = stage.ToString();

    public void Failed(Exception ex) => _failureReason = ex.GetType().Name;

    public string Outcome => _failureReason is not null ? "Failed" : _stage switch
    {
        "Execute" or "RecordOutcome" => "Executed",
        "SafetyValidation" => "Refused",
        _ => ClarificationCount > 0 || _stage is "Ambiguous" or "MissingFields" ? "Clarified" : "Replied",
    };

    /// <summary>The row for this message. Written by the worker in its own scope, so a failed write cannot change the message's own work.</summary>
    public PipelineTrace ToRow(DateTime now) => new()
    {
        TraceId = TraceId, CreatedAt = now, TotalMs = (int)_total.ElapsedMilliseconds, AiCalls = AiCalls, AiMs = AiMs,
        ClarificationCount = ClarificationCount, SelectedAction = _selectedAction, Stage = _stage, Outcome = Outcome, FailureReason = _failureReason,
    };
}
