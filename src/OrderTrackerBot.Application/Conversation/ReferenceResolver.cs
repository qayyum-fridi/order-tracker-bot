using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Application.Conversation;

public enum ReferenceStatus { Resolved, NotFound, Multiple }

/// <summary>One lookup's outcome. <see cref="Id"/> is set only when Resolved; <see cref="Matches"/> lists what was found (up to five).</summary>
public sealed record ReferenceResult(ReferenceStatus Status, int? Id, IReadOnlyList<int> Matches);

/// <summary>
/// Stage 4. Looks up the records a selected action names, always inside the seller's own data.
/// Several matches are <see cref="ReferenceStatus.Multiple"/>, never an arbitrary pick: the caller asks the seller which one.
/// </summary>
public sealed class ReferenceResolver
{
    private const int MaxMatches = 5;
    private static readonly IReadOnlyList<int> None = Array.Empty<int>();

    private readonly IAppDbContext _db;

    public ReferenceResolver(IAppDbContext db) => _db = db;

    public async Task<ReferenceResult> ResolveOrderAsync(int sellerId, int orderId, CancellationToken ct)
    {
        var ids = await _db.Orders
            .Where(o => o.SellerId == sellerId && o.Id == orderId)
            .Select(o => o.Id)
            .Take(1)
            .ToListAsync(ct);
        return ids.Count == 1 ? new(ReferenceStatus.Resolved, ids[0], ids) : new(ReferenceStatus.NotFound, null, None);
    }

    public async Task<ReferenceResult> ResolveCustomerAsync(int sellerId, string name, CancellationToken ct)
    {
        var needle = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (needle.Length == 0) return new(ReferenceStatus.NotFound, null, None);

        // The full name matches exactly one saved customer: that is the customer, not a guess among those containing it.
        var exact = await _db.Customers
            .Where(c => c.SellerId == sellerId && c.DeletedAt == null && c.Name.ToLower() == needle)
            .Select(c => c.Id)
            .Take(2)
            .ToListAsync(ct);
        if (exact.Count == 1) return new(ReferenceStatus.Resolved, exact[0], exact);

        var ids = await _db.Customers
            .Where(c => c.SellerId == sellerId && c.DeletedAt == null && c.Name.ToLower().Contains(needle))
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .Take(MaxMatches)
            .ToListAsync(ct);

        return ids.Count switch
        {
            0 => new(ReferenceStatus.NotFound, null, None),
            1 => new(ReferenceStatus.Resolved, ids[0], ids),
            _ => new(ReferenceStatus.Multiple, null, ids),
        };
    }
}
