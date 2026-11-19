using Microsoft.Extensions.DependencyInjection;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.WhatsApp;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Bugs found by reading src/. Each test states the correct behaviour and FAILS today; it passes once the bug is fixed.
/// </summary>
public class BugHuntTests : IDisposable
{
    private const string Phone = "923001110001";

    private readonly TestDbContextFactory _dbFactory = new();
    private readonly ServiceProvider _services;
    private readonly WebhookWorkQueue _queue = new();
    private readonly WebhookInbox _inbox;

    public BugHuntTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAppDbContext>(_ => _dbFactory.CreateContext());
        _services = services.BuildServiceProvider();
        _inbox = new WebhookInbox(_services.GetRequiredService<IServiceScopeFactory>(), _queue);
    }

    public void Dispose()
    {
        _services.Dispose();
        _dbFactory.Dispose();
    }

    /// <summary>
    /// A USD budget smaller than one use's cost (a mis-set MediaLimits:FreeUsdPerHour, e.g. 0.005 against ImageUsd 0.01) must refuse the use with
    /// BudgetLimit. Consume reads queue.Peek() for the retry time, and the queue is empty, so it throws InvalidOperationException instead.
    /// </summary>
    [Fact]
    public void Limiter_RefusesAUse_WhoseCostExceedsTheWholeBudget_WithoutThrowing()
    {
        var limiter = new MediaRateLimiter(new MediaLimitOptions { ImageUsd = 0.01m });

        var decision = limiter.Consume("923001110001", MediaKind.Image, budgetUsd: 0.005m, out _);

        Assert.Equal(UsageDecision.BudgetLimit, decision);
    }

    /// <summary>
    /// RecoverAsync re-queues messages that never began and tells the seller about the ones that were cut off. When telling one seller throws,
    /// the exception escapes the loop and every later message is neither re-queued nor closed: it stays pending until the next restart.
    /// </summary>
    [Fact]
    public async Task Recover_KeepsGoing_WhenTellingOneSellerFails()
    {
        await _inbox.AcceptAsync(new WebhookMessage("half-done", WebhookMessageKind.Text, Phone, Text: "order 1"));
        await Task.Delay(30);
        await _inbox.AcceptAsync(new WebhookMessage("never-started", WebhookMessageKind.Text, Phone, Text: "order 2"));
        using (var db = _dbFactory.CreateContext())
        {
            db.ProcessedWebhookMessages.Add(new ProcessedWebhookMessage { MessageId = "half-done" });
            await db.SaveChangesAsync();
        }
        var reader = _queue.Reader(WebhookWorkQueue.LaneFor(Phone));
        while (reader.TryRead(out _)) { }

        var (requeued, _) = await _inbox.RecoverAsync(_ => throw new InvalidOperationException("WhatsApp send failed"));

        Assert.Equal(1, requeued);
        Assert.True(reader.TryRead(out var work));
        Assert.Equal("never-started", work.MessageId);
    }
}
