using System.Threading.Channels;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;

namespace OrderTrackerBot.Infrastructure.WhatsApp;

/// <summary>One inbound WhatsApp message waiting to be handled, with what to report if handling throws.</summary>
public sealed record WebhookWork(
    string From, string? MessageId, IssueCode FailureCode, string? FailureDetail, bool NotifySellerOnFailure,
    Func<ConversationEngine, CancellationToken, Task> Handle);

/// <summary>
/// Hand-off between the webhook (acknowledge Meta immediately) and the background worker. Messages are spread over a few
/// lanes by sender, so one seller's messages are handled in the order they arrived while different sellers run in parallel.
/// A full lane refuses new work (the webhook then answers 503 and Meta retries later; the message-id claim drops duplicates).
/// </summary>
public sealed class WebhookWorkQueue
{
    public const int Lanes = 8;
    private const int LaneCapacity = 1000;

    private readonly Channel<WebhookWork>[] _lanes;

    public WebhookWorkQueue()
    {
        _lanes = Enumerable.Range(0, Lanes)
            .Select(_ => Channel.CreateBounded<WebhookWork>(new BoundedChannelOptions(LaneCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            }))
            .ToArray();
    }

    public ChannelReader<WebhookWork> Reader(int lane) => _lanes[lane].Reader;

    public bool TryEnqueue(WebhookWork work) => _lanes[LaneFor(work.From)].Writer.TryWrite(work);

    /// <summary>A stable hash (string.GetHashCode differs per process) so the same sender always uses the same lane.</summary>
    public static int LaneFor(string sender)
    {
        var hash = 17;
        foreach (var c in sender) hash = unchecked(hash * 31 + c);
        return (int)((uint)hash % Lanes);
    }
}
