using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.WhatsApp;
using Xunit;

namespace OrderTrackerBot.Tests;

public class WebhookMessageGateTests : IDisposable
{
    private readonly TestDbContextFactory _dbFactory = new();

    public void Dispose() => _dbFactory.Dispose();

    // A fresh service provider = a fresh app process sharing the same database.
    private WebhookMessageGate NewGate()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAppDbContext>(_ => _dbFactory.CreateContext());
        return new WebhookMessageGate(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task SameMessageId_RunsOnce_EvenAfterARestart()
    {
        var runs = 0;
        Task Handle() { runs++; return Task.CompletedTask; }

        Assert.True(await NewGate().RunOnceAsync("923001", "wamid.A", Handle, default));
        Assert.False(await NewGate().RunOnceAsync("923001", "wamid.A", Handle, default));
        Assert.True(await NewGate().RunOnceAsync("923001", "wamid.B", Handle, default));

        Assert.Equal(2, runs);
        using var db = _dbFactory.CreateContext();
        Assert.Equal(2, await db.ProcessedWebhookMessages.CountAsync());
    }

    [Fact]
    public async Task MissingMessageId_AlwaysRuns()
    {
        var runs = 0;
        var gate = NewGate();
        await gate.RunOnceAsync("923002", null, () => { runs++; return Task.CompletedTask; }, default);
        await gate.RunOnceAsync("923002", null, () => { runs++; return Task.CompletedTask; }, default);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task FailedMessage_StaysClaimed_SoARedeliveryIsNotProcessedTwice()
    {
        var gate = NewGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunOnceAsync("923003", "wamid.C", () => throw new InvalidOperationException("boom"), default));

        var ranAgain = false;
        Assert.False(await gate.RunOnceAsync("923003", "wamid.C", () => { ranAgain = true; return Task.CompletedTask; }, default));
        Assert.False(ranAgain);
    }

    [Fact]
    public async Task SameSender_IsHandledOneAtATime_OtherSendersAreNotBlocked()
    {
        var gate = NewGate();
        var releaseFirst = new TaskCompletionSource();
        var firstStarted = new TaskCompletionSource();
        var secondStarted = false;

        var first = gate.RunOnceAsync("923004", "wamid.D1", async () => { firstStarted.SetResult(); await releaseFirst.Task; }, default);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = gate.RunOnceAsync("923004", "wamid.D2", () => { secondStarted = true; return Task.CompletedTask; }, default);

        // A different seller goes straight through while the first one is still busy.
        await gate.RunOnceAsync("923999", "wamid.OTHER", () => Task.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Delay(200);
        Assert.False(secondStarted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondStarted);
    }
}
