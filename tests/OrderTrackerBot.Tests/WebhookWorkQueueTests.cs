using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.WhatsApp;
using Xunit;

namespace OrderTrackerBot.Tests;

public class WebhookWorkQueueTests
{
    private static WebhookWork Work(string from, string id) =>
        new(from, id, IssueCodes.InboundMessageFailed, null, true, (_, _) => Task.CompletedTask);

    [Fact]
    public void A_sender_always_uses_the_same_lane_and_keeps_message_order()
    {
        var queue = new WebhookWorkQueue();
        foreach (var id in new[] { "m1", "m2", "m3" })
            Assert.True(queue.TryEnqueue(Work("923001110001", id)));

        var reader = queue.Reader(WebhookWorkQueue.LaneFor("923001110001"));
        var ids = new List<string?>();
        while (reader.TryRead(out var work)) ids.Add(work.MessageId);

        Assert.Equal(new[] { "m1", "m2", "m3" }, ids);
        Assert.Equal(WebhookWorkQueue.LaneFor("923001110001"), WebhookWorkQueue.LaneFor("923001110001"));
    }

    [Fact]
    public void Different_senders_are_spread_over_the_lanes()
    {
        var lanes = Enumerable.Range(0, 200).Select(i => WebhookWorkQueue.LaneFor($"92300{i:D7}")).Distinct().ToList();

        Assert.True(lanes.Count >= 6, $"expected a spread over the lanes, got {lanes.Count}");
        Assert.All(lanes, l => Assert.InRange(l, 0, WebhookWorkQueue.Lanes - 1));
    }

    [Fact]
    public void A_full_lane_refuses_new_work_so_the_webhook_can_answer_503()
    {
        var queue = new WebhookWorkQueue();
        var accepted = 0;
        while (queue.TryEnqueue(Work("923001110001", $"m{accepted}"))) accepted++;

        Assert.Equal(1000, accepted);
        Assert.False(queue.TryEnqueue(Work("923001110001", "overflow")));
    }
}
