using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Infrastructure.WhatsApp;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebhookMessageKind { Text, Image, Audio, Document, Flow, Unsupported }

/// <summary>An inbound WhatsApp message in a form that can be stored and replayed after a restart.</summary>
public sealed record WebhookMessage(
    string Id, WebhookMessageKind Kind, string From,
    string? Text = null, string? MediaId = null, string? Caption = null, string? FileName = null,
    string? MimeType = null, string? Json = null, string? Type = null)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static WebhookMessage? TryDeserialize(string json)
    {
        try { return JsonSerializer.Deserialize<WebhookMessage>(json); }
        catch (JsonException) { return null; }
    }

    public WebhookWork ToWork() => Kind switch
    {
        WebhookMessageKind.Text => new WebhookWork(From, Id, IssueCodes.InboundMessageFailed, null, true,
            (engine, ct) => engine.HandleIncomingMessageAsync(From, Text!, ct)),
        WebhookMessageKind.Image => new WebhookWork(From, Id, IssueCodes.ScreenshotFailed, null, true,
            (engine, ct) => engine.HandleImageMessageAsync(From, MediaId!, Caption, ct)),
        WebhookMessageKind.Audio => new WebhookWork(From, Id, IssueCodes.VoiceNoteFailed, null, true,
            (engine, ct) => engine.HandleAudioMessageAsync(From, MediaId!, ct)),
        WebhookMessageKind.Document => new WebhookWork(From, Id, IssueCodes.UnsupportedMediaReplyFailed, "media type: document", true,
            (engine, ct) => engine.HandleDocumentMessageAsync(From, MediaId!, FileName, MimeType, ct)),
        WebhookMessageKind.Flow => new WebhookWork(From, Id, IssueCodes.FlowSubmissionFailed, null, true,
            (engine, ct) => engine.HandleFlowSubmissionAsync(From, Json!, ct)),
        _ => new WebhookWork(From, Id, IssueCodes.UnsupportedMediaReplyFailed, $"media type: {Type}", false,
            (engine, ct) => engine.HandleUnsupportedMediaAsync(From, Type ?? "unknown", ct))
    };
}

public enum InboxResult { Accepted, Duplicate, QueueFull }

/// <summary>
/// The durable front door of the webhook. A message is written to <c>PendingWebhookMessages</c> (its id is the primary key, so Meta's
/// retries cannot slip in twice) BEFORE it is queued and BEFORE Meta gets its 200; the row is deleted when handling ends. If the app
/// dies in between, the rows left behind are found at the next start (see <c>WebhookWorkerService</c>) instead of the message being lost.
/// </summary>
public sealed class WebhookInbox
{
    private readonly IServiceScopeFactory _scopes;
    private readonly WebhookWorkQueue _queue;

    public WebhookInbox(IServiceScopeFactory scopes, WebhookWorkQueue queue)
    {
        _scopes = scopes;
        _queue = queue;
    }

    public async Task<InboxResult> AcceptAsync(WebhookMessage message, CancellationToken ct = default)
    {
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.PendingWebhookMessages.Add(new PendingWebhookMessage { MessageId = message.Id, Sender = message.From, PayloadJson = message.Serialize() });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return InboxResult.Duplicate; // already accepted: a Meta retry
            }
        }

        if (_queue.TryEnqueue(message.ToWork())) return InboxResult.Accepted;

        await CompleteAsync(message.Id, CancellationToken.None); // refused: forget it so Meta's retry is accepted later
        return InboxResult.QueueFull;
    }

    public async Task CompleteAsync(string messageId, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        await db.PendingWebhookMessages.Where(m => m.MessageId == messageId).ExecuteDeleteAsync(ct);
    }

    public async Task<List<WebhookMessage>> LoadPendingAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var rows = await db.PendingWebhookMessages.OrderBy(m => m.CreatedAt).ToListAsync(ct);
        return rows.Select(r => WebhookMessage.TryDeserialize(r.PayloadJson)).OfType<WebhookMessage>().ToList();
    }

    /// <summary>True when handling of this message had already begun (its id was claimed) — it may have changed data, so it must not be run again.</summary>
    public async Task<bool> WasStartedAsync(string messageId, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        return await db.ProcessedWebhookMessages.AnyAsync(p => p.MessageId == messageId, ct);
    }

    /// <summary>
    /// Run once at start. Rows left from a crash/restart: a message that never began is queued again; one that had begun is not re-run
    /// (it may already have saved an order) — <paramref name="interrupted"/> tells the seller, then its row is removed.
    /// </summary>
    /// <summary>
    /// Deletes pending rows whose payload can no longer be read (corrupt, or written in an older format). They can never be handled, and
    /// left in place they would be skipped on every start. Returns how many were removed.
    /// </summary>
    public async Task<int> DropUnreadableAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var unreadable = (await db.PendingWebhookMessages.ToListAsync(ct))
            .Where(r => WebhookMessage.TryDeserialize(r.PayloadJson) is null)
            .Select(r => r.MessageId)
            .ToList();
        if (unreadable.Count == 0) return 0;
        await db.PendingWebhookMessages.Where(m => unreadable.Contains(m.MessageId)).ExecuteDeleteAsync(ct);
        return unreadable.Count;
    }

    public async Task<(int Requeued, int Interrupted)> RecoverAsync(Func<WebhookMessage, Task> interrupted, CancellationToken ct = default)
    {
        int requeued = 0, cutOff = 0;
        foreach (var message in await LoadPendingAsync(ct))
        {
            if (await WasStartedAsync(message.Id, ct))
            {
                try { await interrupted(message); }
                finally { await CompleteAsync(message.Id, CancellationToken.None); }
                cutOff++;
            }
            else if (_queue.TryEnqueue(message.ToWork())) requeued++; // the row stays until the worker finishes it
        }
        return (requeued, cutOff);
    }
}
