using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Infrastructure.Instagram;
using OrderTrackerBot.Api.Admin;

namespace OrderTrackerBot.Api;

/// <summary>Wakes every 15 minutes to send due proactive messages (weekly summary, trial-ending reminder) and refresh expiring Instagram tokens.</summary>
public sealed class ScheduledMessagesService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ScheduledMessagesService> _logger;

    public ScheduledMessagesService(IServiceScopeFactory scopes, ILogger<ScheduledMessagesService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    /// <summary>Raw message bodies older than the hot window are replaced by a marker. The metadata (time, phone, direction) stays for volume reports.</summary>
    private const int HotMessageDays = 90;
    private const string ArchivedText = "[archived]";

    private static Task<int> ArchiveOldMessagesAsync(IAppDbContext db, DateTime nowUtc, CancellationToken ct) =>
        db.MessageLogs.Where(m => m.CreatedAt < nowUtc.AddDays(-HotMessageDays) && m.RawText != ArchivedText)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RawText, ArchivedText), ct);

    private async Task RunStepAsync(string name, Func<Task> step, CancellationToken stoppingToken)
    {
        try
        {
            await step();
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Scheduled step {Step} failed", name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ConversationEngine>().RunScheduledJobsAsync(DateTime.UtcNow, stoppingToken);
                await UnplacedReport.PurgeOlderThanAsync(scope.ServiceProvider.GetRequiredService<IAppDbContext>(), DateTime.UtcNow, stoppingToken);
                // Each learning / wording step runs on its own: a failure in one is logged and does not stop the others or the token refresh.
                await RunStepAsync("unknown number words", () => UnknownNumberWordLog.FlushAsync(scope.ServiceProvider.GetRequiredService<IAppDbContext>(), DateTime.UtcNow, stoppingToken), stoppingToken);
                await RunStepAsync("keywords", () => KeywordStore.SyncAsync(scope.ServiceProvider.GetRequiredService<IAppDbContext>(), stoppingToken), stoppingToken);
                await RunStepAsync("message templates", () => TemplateStore.SyncAsync(scope.ServiceProvider.GetRequiredService<IAppDbContext>(), stoppingToken), stoppingToken);
                await RunStepAsync("learned keywords", () => LearnedKeywordLog.FlushAsync(scope.ServiceProvider.GetRequiredService<IAppDbContext>(), DateTime.UtcNow, stoppingToken), stoppingToken);
                await RunStepAsync("message retention", () => ArchiveOldMessagesAsync(scope.ServiceProvider.GetRequiredService<IAppDbContext>(), DateTime.UtcNow, stoppingToken), stoppingToken);
                await RunStepAsync("instagram tokens", () => scope.ServiceProvider.GetRequiredService<InstagramClient>().RefreshExpiringTokensAsync(
                    scope.ServiceProvider.GetRequiredService<IAppDbContext>(), DateTime.UtcNow, stoppingToken), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled messages run failed");
                using var reportScope = _scopes.CreateScope();
                await reportScope.ServiceProvider.GetRequiredService<IIssueReporter>().ReportAsync(IssueCodes.ScheduledJobFailed, null, null, ex, stoppingToken);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
