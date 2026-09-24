using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Instagram;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ConversationEngine>().RunScheduledJobsAsync(DateTime.UtcNow, stoppingToken);
                await scope.ServiceProvider.GetRequiredService<InstagramClient>().RefreshExpiringTokensAsync(
                    scope.ServiceProvider.GetRequiredService<IAppDbContext>(), DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduled messages run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
