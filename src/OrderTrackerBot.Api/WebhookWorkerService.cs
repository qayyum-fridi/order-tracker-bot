using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.WhatsApp;

namespace OrderTrackerBot.Api;

/// <summary>
/// Handles queued WhatsApp messages outside the webhook request, so a slow AI call can no longer make Meta time out and retry, and
/// a dropped connection can no longer cancel a message half-way. Each message gets its own scope and its own timeout, and goes
/// through <see cref="WebhookMessageGate"/> exactly as before (per-sender lock + message-id claim). When a message ends, its
/// pending row is deleted. At start, rows left over from a crash or restart are recovered: a message that never started is queued
/// again; one that had already started is NOT re-run (it may have saved an order) — the seller is asked to send it again.
/// </summary>
public sealed class WebhookWorkerService : BackgroundService
{
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromMinutes(3);

    private readonly WebhookWorkQueue _queue;
    private readonly WebhookInbox _inbox;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WebhookWorkerService> _logger;

    public WebhookWorkerService(WebhookWorkQueue queue, WebhookInbox inbox, IServiceScopeFactory scopes, ILogger<WebhookWorkerService> logger)
    {
        _queue = queue;
        _inbox = inbox;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await Task.WhenAll(Enumerable.Range(0, WebhookWorkQueue.Lanes).Select(lane => Task.Run(() => RunLaneAsync(lane, stoppingToken), CancellationToken.None)));
    }

    private async Task RecoverAsync(CancellationToken stoppingToken)
    {
        try
        {
            var (requeued, interrupted) = await _inbox.RecoverAsync(AskToResendAsync, stoppingToken);
            if (requeued + interrupted > 0)
                _logger.LogWarning("Recovered WhatsApp messages from the last run: {Requeued} queued again, {Interrupted} cut off mid-way (sellers asked to resend)", requeued, interrupted);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not recover pending WhatsApp messages");
        }
    }

    private async Task AskToResendAsync(WebhookMessage message)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IWhatsAppSender>().SendTextMessageAsync(message.From,
                "⚠️ Server restart ki wajah se aapka pichla message poora process nahi ho saka — meherbani karke dobara bhej dein. Kuch delete nahi hua.");
            await scope.ServiceProvider.GetRequiredService<IIssueReporter>().ReportAsync(
                IssueCodes.WebhookMessageInterrupted, message.From, $"{message.Kind} message {message.Id}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ask {From} to resend an interrupted message", message.From);
        }
    }

    private async Task RunLaneAsync(int lane, CancellationToken stoppingToken)
    {
        var reader = _queue.Reader(lane);
        try
        {
            await foreach (var work in reader.ReadAllAsync(stoppingToken))
                await ProcessAsync(work);
        }
        catch (OperationCanceledException) { }

        var left = 0;
        while (reader.TryRead(out _)) left++;
        if (left > 0) _logger.LogWarning("Webhook lane {Lane} stopped with {Count} message(s) still queued; they are recovered at the next start", lane, left);
    }

    private async Task ProcessAsync(WebhookWork work)
    {
        // Not tied to the app-stopping token: the message in hand is finished (bounded by its own timeout) before the lane stops.
        using var timeout = new CancellationTokenSource(MessageTimeout);
        var ct = timeout.Token;
        using var scope = _scopes.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ConversationEngine>();
        try
        {
            var gate = scope.ServiceProvider.GetRequiredService<WebhookMessageGate>();
            await gate.RunOnceAsync(work.From, work.MessageId, () => work.Handle(engine, ct), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process inbound WhatsApp message from {From} ({Code})", work.From, work.FailureCode.Code);
            try
            {
                var issues = scope.ServiceProvider.GetRequiredService<IIssueReporter>();
                await issues.ReportAsync(work.FailureCode, work.From, work.FailureDetail, ex, CancellationToken.None);
                if (work.NotifySellerOnFailure)
                    await engine.SendSystemErrorAsync(work.From, work.FailureCode.Code, CancellationToken.None);
            }
            catch (Exception reportEx)
            {
                _logger.LogWarning(reportEx, "Could not report failure for message from {From}", work.From);
            }
        }
        finally
        {
            // Done (or failed and already reported): the message must not be recovered again.
            if (work.MessageId is not null)
            {
                try { await _inbox.CompleteAsync(work.MessageId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not clear pending message {Id}", work.MessageId); }
            }
        }
    }
}
