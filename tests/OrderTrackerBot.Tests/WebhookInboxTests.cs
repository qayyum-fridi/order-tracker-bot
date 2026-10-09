using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.WhatsApp;
using Xunit;

namespace OrderTrackerBot.Tests;

public class WebhookInboxTests : IDisposable
{
    private const string Phone = "923001110001";

    private readonly TestDbContextFactory _dbFactory = new();
    private readonly ServiceProvider _services;
    private readonly WebhookWorkQueue _queue = new();
    private readonly WebhookInbox _inbox;

    public WebhookInboxTests()
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

    private static WebhookMessage Text(string id, string text = "hello") => new(id, WebhookMessageKind.Text, Phone, Text: text);

    private async Task<List<string>> PendingIdsAsync()
    {
        using var db = _dbFactory.CreateContext();
        return await db.PendingWebhookMessages.OrderBy(m => m.CreatedAt).Select(m => m.MessageId).ToListAsync();
    }

    [Fact]
    public async Task An_accepted_message_is_stored_and_queued()
    {
        Assert.Equal(InboxResult.Accepted, await _inbox.AcceptAsync(Text("wamid.1")));

        Assert.Equal(new[] { "wamid.1" }, await PendingIdsAsync());
        Assert.True(_queue.Reader(WebhookWorkQueue.LaneFor(Phone)).TryRead(out var work));
        Assert.Equal("wamid.1", work.MessageId);
    }

    [Fact]
    public async Task A_retry_of_the_same_message_id_is_a_duplicate_and_is_not_queued_twice()
    {
        await _inbox.AcceptAsync(Text("wamid.1"));

        Assert.Equal(InboxResult.Duplicate, await _inbox.AcceptAsync(Text("wamid.1")));

        var reader = _queue.Reader(WebhookWorkQueue.LaneFor(Phone));
        Assert.True(reader.TryRead(out _));
        Assert.False(reader.TryRead(out _));
        Assert.Single(await PendingIdsAsync());
    }

    [Fact]
    public async Task A_full_queue_refuses_and_forgets_the_message_so_a_later_retry_works()
    {
        for (var i = 0; i < 1000; i++) Assert.Equal(InboxResult.Accepted, await _inbox.AcceptAsync(Text($"fill-{i}")));

        Assert.Equal(InboxResult.QueueFull, await _inbox.AcceptAsync(Text("overflow")));

        Assert.DoesNotContain("overflow", await PendingIdsAsync());
    }

    [Fact]
    public async Task Complete_removes_the_pending_row()
    {
        await _inbox.AcceptAsync(Text("wamid.1"));

        await _inbox.CompleteAsync("wamid.1");

        Assert.Empty(await PendingIdsAsync());
    }

    [Fact]
    public void Every_kind_of_message_survives_being_stored_and_loaded()
    {
        var messages = new[]
        {
            new WebhookMessage("a", WebhookMessageKind.Text, Phone, Text: "order Ayesha 2 suit"),
            new WebhookMessage("b", WebhookMessageKind.Image, Phone, MediaId: "m1", Caption: "logo"),
            new WebhookMessage("c", WebhookMessageKind.Audio, Phone, MediaId: "m2"),
            new WebhookMessage("d", WebhookMessageKind.Document, Phone, MediaId: "m3", FileName: "x.xlsx", MimeType: "application/vnd.ms-excel"),
            new WebhookMessage("e", WebhookMessageKind.Flow, Phone, Json: "{\"k\":1}"),
            new WebhookMessage("f", WebhookMessageKind.Unsupported, Phone, Type: "sticker")
        };

        foreach (var message in messages)
            Assert.Equal(message, WebhookMessage.TryDeserialize(message.Serialize()));
    }

    [Fact]
    public async Task After_a_restart_unstarted_messages_are_queued_again_and_started_ones_are_not_re_run()
    {
        await _inbox.AcceptAsync(Text("never-started", "order 1"));
        await _inbox.AcceptAsync(Text("half-done", "order 2"));
        using (var db = _dbFactory.CreateContext())
        {
            db.ProcessedWebhookMessages.Add(new ProcessedWebhookMessage { MessageId = "half-done" }); // its handling had begun
            await db.SaveChangesAsync();
        }
        var reader = _queue.Reader(WebhookWorkQueue.LaneFor(Phone));
        while (reader.TryRead(out _)) { } // the old process's in-memory queue is gone
        var told = new List<string>();

        var (requeued, interrupted) = await _inbox.RecoverAsync(m => { told.Add(m.Id); return Task.CompletedTask; });

        Assert.Equal((1, 1), (requeued, interrupted));
        Assert.Equal(new[] { "half-done" }, told);
        Assert.True(reader.TryRead(out var work));
        Assert.Equal("never-started", work.MessageId);
        Assert.Equal(new[] { "never-started" }, await PendingIdsAsync()); // the cut-off one is closed; the re-queued one stays until finished
    }
}
