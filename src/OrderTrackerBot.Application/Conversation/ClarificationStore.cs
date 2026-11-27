using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Stage 6 persistence. Moves <see cref="PendingAction"/> records in and out of the PendingActions table.
/// Every lookup is scoped to one seller and one user, so a reply from anyone else finds nothing.
/// Writes are staged in the caller's unit of work and saved with the rest of the message (the engine saves once per message).
/// </summary>
public sealed class ClarificationStore
{
    private readonly IAppDbContext _db;

    public ClarificationStore(IAppDbContext db) => _db = db;

    /// <summary>Adds or updates the record in the unit of work. Does not call SaveChanges.</summary>
    public async Task TrackAsync(PendingAction pending, CancellationToken ct)
    {
        var record = _db.PendingActions.Local.FirstOrDefault(p => p.Id == pending.Id)
                     ?? await _db.PendingActions.FirstOrDefaultAsync(p => p.Id == pending.Id, ct);
        if (record is null)
        {
            record = new PendingActionRecord { Id = pending.Id };
            _db.PendingActions.Add(record);
        }
        record.SellerId = pending.SellerId;
        record.UserId = pending.UserId;
        record.Action = pending.Action;
        record.ResolvedReferencesJson = JsonSerializer.Serialize(pending.ResolvedReferences);
        record.MissingFieldsJson = JsonSerializer.Serialize(pending.MissingFields);
        record.AnswersJson = JsonSerializer.Serialize(pending.Answers);
        record.CurrentQuestion = pending.CurrentQuestion;
        record.CreatedAt = pending.CreatedAt;
        record.ExpiresAt = pending.ExpiresAt;
        record.Status = pending.Status.ToString();
    }

    /// <summary>The record with this id, only if it belongs to this seller and user; any status. Null otherwise.</summary>
    public async Task<PendingAction?> FindAsync(string id, int sellerId, string userId, CancellationToken ct)
    {
        var record = await _db.PendingActions.FirstOrDefaultAsync(p => p.Id == id && p.SellerId == sellerId && p.UserId == userId, ct);
        return record is null ? null : ToPending(record);
    }

    /// <summary>The seller's newest waiting question for this user that has not expired; null when there is none.</summary>
    public async Task<PendingAction?> FindWaitingAsync(int sellerId, string userId, DateTime now, CancellationToken ct)
    {
        var record = await _db.PendingActions
            .Where(p => p.SellerId == sellerId && p.UserId == userId && p.Status == "Waiting" && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return record is null ? null : ToPending(record);
    }

    /// <summary>A new question replaces any older one still waiting for this seller and user: those are marked Cancelled.</summary>
    public async Task CancelWaitingAsync(int sellerId, string userId, CancellationToken ct)
    {
        var waiting = await _db.PendingActions.Where(p => p.SellerId == sellerId && p.UserId == userId && p.Status == "Waiting").ToListAsync(ct);
        foreach (var record in waiting) record.Status = nameof(PendingStatus.Cancelled);
    }

    public static PendingAction ToPending(PendingActionRecord record)
    {
        var pending = new PendingAction
        {
            Id = record.Id, SellerId = record.SellerId, UserId = record.UserId, Action = record.Action,
            CurrentQuestion = record.CurrentQuestion, CreatedAt = record.CreatedAt, ExpiresAt = record.ExpiresAt,
            Status = Enum.Parse<PendingStatus>(record.Status),
        };
        foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, int>>(record.ResolvedReferencesJson) ?? new()) pending.ResolvedReferences[kv.Key] = kv.Value;
        pending.MissingFields.AddRange(JsonSerializer.Deserialize<List<string>>(record.MissingFieldsJson) ?? new());
        foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(record.AnswersJson) ?? new()) pending.Answers[kv.Key] = kv.Value;
        return pending;
    }
}
