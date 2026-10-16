using System.Collections.Concurrent;

namespace OrderTrackerBot.Api.Client;

/// <summary>
/// Uploads kept between the dashboard's preview and commit. In memory only: 15 minutes, one seller's uploads visible to
/// that seller only, and a cap on how many can wait at once. After a restart the seller uploads the file again.
/// </summary>
public sealed class ClientImportStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public const int MaxWaiting = 200;

    private readonly ConcurrentDictionary<string, Waiting> _items = new();

    public sealed record Waiting(int SellerId, string Kind, string FileName, byte[] Bytes, DateTime ExpiresUtc);

    /// <summary>Keeps an upload and returns its id, or null when too many are waiting.</summary>
    public string? Add(int sellerId, string kind, string fileName, byte[] bytes, DateTime nowUtc)
    {
        Sweep(nowUtc);
        if (_items.Count >= MaxWaiting) return null;
        var id = Guid.NewGuid().ToString("N");
        _items[id] = new Waiting(sellerId, kind, fileName, bytes, nowUtc + Lifetime);
        return id;
    }

    /// <summary>Takes the upload out of the store. Returns null when it is unknown, expired, or belongs to another seller.</summary>
    public Waiting? Take(string id, int sellerId, DateTime nowUtc)
    {
        if (!_items.TryRemove(id, out var waiting)) return null;
        return waiting.SellerId == sellerId && waiting.ExpiresUtc > nowUtc ? waiting : null;
    }

    private void Sweep(DateTime nowUtc)
    {
        foreach (var (id, waiting) in _items)
            if (waiting.ExpiresUtc <= nowUtc) _items.TryRemove(id, out _);
    }
}
