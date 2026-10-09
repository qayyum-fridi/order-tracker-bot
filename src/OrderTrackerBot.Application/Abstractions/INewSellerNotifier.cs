namespace OrderTrackerBot.Application.Abstractions;

/// <summary>Tells the founder a brand-new seller just messaged the bot for the first time. Implementations must never throw or make the seller wait.</summary>
public interface INewSellerNotifier
{
    Task SellerRegisteredAsync(string phone, int totalSellers, CancellationToken cancellationToken = default);
}
