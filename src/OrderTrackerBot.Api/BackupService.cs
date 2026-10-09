using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.Backup;

namespace OrderTrackerBot.Api;

/// <summary>Checks every 10 minutes whether the newest Drive backup is older than <c>Backup:IntervalHours</c> and takes a new one if so. Survives restarts because "last backup" is read from Drive.</summary>
public sealed class BackupService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AlertEvery = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly BackupOptions _options;
    private readonly ILogger<BackupService> _logger;

    public BackupService(IServiceScopeFactory scopes, IConfiguration configuration, IOptions<BackupOptions> options, ILogger<BackupService> logger)
    {
        _scopes = scopes;
        _configuration = configuration;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dbPath = SqliteBackupManager.ResolveDbPath(_configuration.GetConnectionString("Default")!);
        var interval = TimeSpan.FromHours(Math.Max(1, _options.IntervalHours));
        DateTime? lastBackup = null;
        DateTime lastAlert = DateTime.MinValue;

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // let the app finish starting
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var manager = scope.ServiceProvider.GetRequiredService<SqliteBackupManager>();
                lastBackup ??= await manager.LastBackupUtcAsync(stoppingToken) ?? DateTime.MinValue;
                if (DateTime.UtcNow - lastBackup >= interval)
                {
                    await manager.BackupAsync(dbPath, stoppingToken);
                    lastBackup = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Database backup failed; will retry in {Minutes} minutes", Tick.TotalMinutes);
                if (DateTime.UtcNow - lastAlert >= AlertEvery)
                {
                    lastAlert = DateTime.UtcNow;
                    using var reportScope = _scopes.CreateScope();
                    await reportScope.ServiceProvider.GetRequiredService<IIssueReporter>()
                        .ReportAsync(IssueCodes.BackupFailed, null, null, ex, stoppingToken);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
