using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Infrastructure.WhatsApp;

/// <summary>
/// Runs each inbound WhatsApp message at most once and one at a time per sender:
/// <list type="bullet">
/// <item>Per-sender lock — two quick messages from the same seller can't race on their ConversationSession (in-process only;
/// several app instances would need sticky routing or a DB lock).</item>
/// <item>Once — the message id is claimed in the DB (primary key) before handling, so Meta's redeliveries are dropped even after
/// a restart. The claim uses its own DbContext so it never flushes half-done changes from an earlier failed message.</item>
/// </list>
/// A message whose handling throws stays claimed: the seller already got the error reply and can resend.
/// </summary>
public sealed class WebhookMessageGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SenderLocks = new();
    private readonly IServiceScopeFactory _scopes;

    public WebhookMessageGate(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <summary>Returns false (and skips <paramref name="handle"/>) when this message id was already processed.</summary>
    public async Task<bool> RunOnceAsync(string sender, string? messageId, Func<Task> handle, CancellationToken ct)
    {
        var gate = SenderLocks.GetOrAdd(sender, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!await TryClaimAsync(messageId, ct)) return false;
            await handle();
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> TryClaimAsync(string? messageId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(messageId)) return true;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.ProcessedWebhookMessages.Add(new ProcessedWebhookMessage { MessageId = messageId });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }
}
