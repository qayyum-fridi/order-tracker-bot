namespace OrderTrackerBot.Application.Abstractions;

/// <summary>Fires when a merchant complains/comments about the bot itself (not a customer complaint) — routed to the founder, e.g. via an n8n webhook to Telegram/email.</summary>
public interface IFounderAlertNotifier
{
    Task NotifyAsync(int sellerId, string message, CancellationToken cancellationToken = default);
}
