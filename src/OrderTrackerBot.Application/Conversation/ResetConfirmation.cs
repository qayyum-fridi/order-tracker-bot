using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Validity and claim rules for "reset account" YES. A prompt is valid for <see cref="Window"/> (exclusive: at exactly the window it has expired).
/// </summary>
public static class ResetConfirmation
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>True when the prompt has both a timestamp and a generation id, and its age is inside the window. A timestamp in the future is invalid.</summary>
    public static bool IsValid(DateTime? askedAtUtc, string? generationId, DateTime nowUtc)
    {
        if (askedAtUtc is null || string.IsNullOrEmpty(generationId)) return false;
        var age = nowUtc - askedAtUtc.Value;
        return age >= TimeSpan.Zero && age < Window;
    }

    /// <summary>
    /// Moves the session out of AwaitingResetConfirmation, only if it is still waiting for this generation. Only one caller can win.
    /// The generation is looked up in the stored session context, so the claim is for the exact prompt the YES was checked against.
    /// </summary>
    public static async Task<bool> TryClaimAsync(IAppDbContext db, int sessionId, string generationId, CancellationToken ct)
    {
        var claimed = await db.Sessions
            .Where(s => s.Id == sessionId && s.State == ConversationState.AwaitingResetConfirmation && s.ContextJson.Contains(generationId))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ConversationState.Idle), ct);
        return claimed == 1;
    }
}
