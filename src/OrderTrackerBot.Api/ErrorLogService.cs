using Microsoft.Extensions.Options;
using OrderTrackerBot.Infrastructure.Alerts;

namespace OrderTrackerBot.Api;

/// <summary>Flushes the in-memory error buffer to the daily CSV / Drive / email every <c>ErrorLog:FlushMinutes</c>, and once more on shutdown.</summary>
public sealed class ErrorLogService : BackgroundService
{
    private readonly ErrorLogWriter _writer;
    private readonly TimeSpan _interval;
    private readonly ILogger<ErrorLogService> _logger;

    public ErrorLogService(ErrorLogWriter writer, IOptions<ErrorLogOptions> options, ILogger<ErrorLogService> logger)
    {
        _writer = writer;
        _interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.FlushMinutes));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await _writer.FlushAsync(stoppingToken); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Error log flush failed");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _writer.FlushAsync(limit.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final error log flush failed");
        }
    }
}
