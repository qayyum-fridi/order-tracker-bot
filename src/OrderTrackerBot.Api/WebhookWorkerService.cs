using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.WhatsApp;

namespace OrderTrackerBot.Api;

/// <summary>
/// Handles queued WhatsApp messages outside the webhook request, so a slow AI call can no longer make Meta time out and retry, and
/// a dropped connection can no longer cancel a message half-way. Each message gets its own scope and its own timeout, and goes
/// through <see cref="WebhookMessageGate"/> exactly as before (per-sender lock + message-id claim).
/// Messages still waiting when the app stops are not processed (Meta already got its 200); a warning says how many.
/// </summary>
public sealed class WebhookWorkerService : BackgroundService
{
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromMinutes(3);

    private readonly WebhookWorkQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WebhookWorkerService> _logger;

    public WebhookWorkerService(WebhookWorkQueue queue, IServiceScopeFactory scopes, ILogger<WebhookWorkerService> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, WebhookWorkQueue.Lanes).Select(lane => Task.Run(() => RunLaneAsync(lane, stoppingToken), CancellationToken.None)));

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
        if (left > 0) _logger.LogWarning("Webhook lane {Lane} stopped with {Count} unprocessed message(s)", lane, left);
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
    }
}
