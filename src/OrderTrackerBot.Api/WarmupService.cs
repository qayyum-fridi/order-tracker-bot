using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Ai;
using OrderTrackerBot.Infrastructure.Persistence;
using OrderTrackerBot.Infrastructure.WhatsApp;

namespace OrderTrackerBot.Api;

/// <summary>
/// Keeps the first message after a quiet spell as fast as the rest: JIT-compiles the hot path once at start, then every minute touches the
/// database and re-uses the OpenAI / Meta connections so their TLS sessions and pooled sockets are not dropped while nobody is chatting.
/// Best-effort: every failure is ignored.
/// </summary>
public sealed class WarmupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly IOptions<OpenAiOptions> _openAi;
    private readonly IOptions<WhatsAppOptions> _whatsApp;
    private readonly ILogger<WarmupService> _logger;

    public WarmupService(IServiceScopeFactory scopes, IHttpClientFactory http, IOptions<OpenAiOptions> openAi, IOptions<WhatsAppOptions> whatsApp, ILogger<WarmupService> logger)
    {
        _scopes = scopes;
        _http = http;
        _openAi = openAi;
        _whatsApp = whatsApp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            _ = CommandParser.TryParse("orders today");
            _ = CommandParser.TryParse("mark 3 shipped");
            _ = scope.ServiceProvider.GetRequiredService<ConversationEngine>();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Warm-up of the hot path failed"); }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await TouchAsync(stoppingToken);
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task TouchAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Sellers.AsNoTracking().Select(s => s.Id).Take(1).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Warm-up database touch failed"); }

        // Same named clients as the typed ones, so the same pooled connections are kept alive.
        await PingAsync(typeof(IAiOrderAssistant).Name, $"{_openAi.Value.BaseUrl.TrimEnd('/')}/models", _openAi.Value.ApiKey, ct);
        await PingAsync(typeof(IWhatsAppSender).Name, _whatsApp.Value.GraphApiBaseUrl.TrimEnd('/'), null, ct);
    }

    private async Task PingAsync(string clientName, string url, string? bearer, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            if (!string.IsNullOrWhiteSpace(bearer)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            using var response = await _http.CreateClient(clientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Warm-up ping to {Url} failed", url);
        }
    }
}
