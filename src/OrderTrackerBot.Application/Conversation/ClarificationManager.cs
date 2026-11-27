namespace OrderTrackerBot.Application.Conversation;

public enum PendingStatus { Waiting, Completed, Expired, Cancelled }

/// <summary>
/// Stage 6. One question the bot is waiting on for one seller and one user. The reply is bound to this record: it is matched to the
/// current missing field and never goes straight to a database handler. When every field is answered the action is handed back
/// for the reference and safety checks to run again (see <see cref="ClarificationOutcome.Ready"/>).
/// </summary>
public sealed class PendingAction
{
    public required string Id { get; init; }
    public required int SellerId { get; init; }
    public required string UserId { get; init; }
    /// <summary>The action the seller asked for (its name, e.g. "create_order").</summary>
    public required string Action { get; init; }
    /// <summary>Record ids that were resolved for this action (field name to id), checked again before execution.</summary>
    public Dictionary<string, int> ResolvedReferences { get; } = new();
    /// <summary>Fields still needed, in the order they will be asked. The first one is the current question's field.</summary>
    public List<string> MissingFields { get; } = new();
    /// <summary>Values the seller has already given, by field. These are not asked again.</summary>
    public Dictionary<string, string> Answers { get; } = new();
    public required string CurrentQuestion { get; set; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public PendingStatus Status { get; set; } = PendingStatus.Waiting;
}

public enum ClarificationOutcome { Ask, Invalid, Ready, Expired, Closed, NotForThisPending }

public sealed record ClarificationResult(ClarificationOutcome Outcome, PendingAction Pending, string? Question = null);

/// <summary>
/// Creates pending actions, decides whether a reply belongs to one, validates it against the field being asked, and
/// moves the record on. It never executes anything: <see cref="ClarificationOutcome.Ready"/> means "run the reference and
/// safety checks, then execute", and the caller does that.
/// </summary>
public static class ClarificationManager
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    public static PendingAction Create(
        string id, int sellerId, string userId, string action, IReadOnlyList<string> missingFields, string firstQuestion,
        DateTime now, TimeSpan? ttl = null)
    {
        if (missingFields.Count == 0) throw new ArgumentException("A pending action needs at least one missing field.", nameof(missingFields));
        var pending = new PendingAction
        {
            Id = id, SellerId = sellerId, UserId = userId, Action = action,
            CurrentQuestion = firstQuestion, CreatedAt = now, ExpiresAt = now + (ttl ?? DefaultTtl),
        };
        pending.MissingFields.AddRange(missingFields);
        return pending;
    }

    /// <summary>True only when this reply comes from the same seller and user the question was asked to.</summary>
    public static bool Belongs(PendingAction pending, int sellerId, string userId) =>
        pending.SellerId == sellerId && string.Equals(pending.UserId, userId, StringComparison.Ordinal);

    /// <summary>
    /// Applies one reply. <paramref name="parse"/> validates the reply for the current field: it returns the value to keep, or null when the reply
    /// does not fit that field (then the same question is asked again, and only that field).
    /// </summary>
    public static ClarificationResult Answer(PendingAction pending, int sellerId, string userId, string reply, DateTime now, Func<string, string, string?> parse)
    {
        if (!Belongs(pending, sellerId, userId)) return new(ClarificationOutcome.NotForThisPending, pending);
        if (pending.Status != PendingStatus.Waiting) return new(ClarificationOutcome.Closed, pending);
        if (now >= pending.ExpiresAt)
        {
            pending.Status = PendingStatus.Expired;
            return new(ClarificationOutcome.Expired, pending);
        }

        var field = pending.MissingFields[0];
        var value = parse(field, reply);
        if (value is null) return new(ClarificationOutcome.Invalid, pending, pending.CurrentQuestion);

        pending.Answers[field] = value;
        pending.MissingFields.RemoveAt(0);
        if (pending.MissingFields.Count == 0)
            return new(ClarificationOutcome.Ready, pending);

        pending.CurrentQuestion = QuestionFor(pending.MissingFields[0]);
        return new(ClarificationOutcome.Ask, pending, pending.CurrentQuestion);
    }

    /// <summary>Marks the record done once the action has run. Nothing else may change a completed record.</summary>
    public static void Complete(PendingAction pending) => pending.Status = PendingStatus.Completed;

    public static void Cancel(PendingAction pending) => pending.Status = PendingStatus.Cancelled;

    /// <summary>Closes a question whose time ran out. A record that is already closed keeps its status.</summary>
    public static void Expire(PendingAction pending)
    {
        if (pending.Status == PendingStatus.Waiting) pending.Status = PendingStatus.Expired;
    }

    /// <summary>Plain wording for one field's question. The engine's templates replace this when the record is wired in.</summary>
    public static string QuestionFor(string field) => $"Barae meherbani {field} batayein.";
}
