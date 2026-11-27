using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Application.Conversation;

public enum ReferenceStatus { Resolved, NotFound, Multiple }

/// <summary>How a name matched: the whole saved name, or only part of it.</summary>
public enum MatchKind { Exact, Partial }

/// <summary>
/// One lookup's outcome. <see cref="Id"/> is set only when Resolved; <see cref="Matches"/> lists what was found (up to five);
/// <see cref="Kind"/> is set for name lookups.
/// </summary>
public sealed record ReferenceResult(ReferenceStatus Status, int? Id, IReadOnlyList<int> Matches, MatchKind? Kind = null);

/// <summary>
/// Stage 4. Looks up the records a selected action names, always inside the seller's own data.
/// Several matches are <see cref="ReferenceStatus.Multiple"/>, never an arbitrary pick: the caller asks the seller which one.
/// Every lookup takes the seller id, and every record it returns is checked against that seller.
/// </summary>
public sealed class ReferenceResolver
{
    private const int MaxMatches = 5;
    private static readonly IReadOnlyList<int> None = Array.Empty<int>();

    private readonly IAppDbContext _db;

    public ReferenceResolver(IAppDbContext db) => _db = db;

    /// <summary>An order id: the order must belong to the seller, and so must its customer (a customer's seller is checked on its own record).</summary>
    public async Task<ReferenceResult> ResolveOrderAsync(int sellerId, int orderId, CancellationToken ct)
    {
        var ids = await _db.Orders
            .Where(o => o.SellerId == sellerId && o.Id == orderId && o.Customer!.SellerId == sellerId)
            .Select(o => o.Id)
            .Take(1)
            .ToListAsync(ct);
        return ids.Count == 1 ? new(ReferenceStatus.Resolved, ids[0], ids) : new(ReferenceStatus.NotFound, null, None);
    }

    /// <summary>A customer id taken from a tap or a re-check before execution: resolved only if this seller still owns it.</summary>
    public async Task<ReferenceResult> ResolveCustomerByIdAsync(int sellerId, int customerId, CancellationToken ct)
    {
        var ids = await _db.Customers
            .Where(c => c.SellerId == sellerId && c.Id == customerId && c.DeletedAt == null)
            .Select(c => c.Id)
            .Take(1)
            .ToListAsync(ct);
        return ids.Count == 1 ? new(ReferenceStatus.Resolved, ids[0], ids, MatchKind.Exact) : new(ReferenceStatus.NotFound, null, None);
    }

    /// <summary>A product id taken from a tap or a re-check before execution: resolved only if this seller still owns it and it is active.</summary>
    public async Task<ReferenceResult> ResolveProductByIdAsync(int sellerId, int productId, CancellationToken ct)
    {
        var ids = await _db.Products
            .Where(p => p.SellerId == sellerId && p.Id == productId && p.IsActive)
            .Select(p => p.Id)
            .Take(1)
            .ToListAsync(ct);
        return ids.Count == 1 ? new(ReferenceStatus.Resolved, ids[0], ids, MatchKind.Exact) : new(ReferenceStatus.NotFound, null, None);
    }

    public async Task<ReferenceResult> ResolveCustomerAsync(int sellerId, string name, CancellationToken ct)
    {
        var needle = Needle(name);
        if (needle.Length == 0) return None_();
        // Every exact match also contains the needle, so one SQL-side filter finds both kinds; the split happens here.
        var rows = await _db.Customers
            .Where(c => c.SellerId == sellerId && c.DeletedAt == null && c.Name.ToLower().Contains(needle))
            .OrderBy(c => c.Name.Length).ThenBy(c => c.Id)   // an exact name is the shortest containing one, so it survives the Take
            .Select(c => new { c.Id, c.Name })
            .Take(MaxMatches * 4)
            .ToListAsync(ct);
        return Classify(rows.Select(r => (r.Id, r.Name)).ToList(), needle);
    }

    public async Task<ReferenceResult> ResolveProductAsync(int sellerId, string name, CancellationToken ct)
    {
        var needle = Needle(name);
        if (needle.Length == 0) return None_();
        var rows = await _db.Products
            .Where(p => p.SellerId == sellerId && p.IsActive && p.Name.ToLower().Contains(needle))
            .OrderBy(p => p.Name.Length).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Name })
            .Take(MaxMatches * 4)
            .ToListAsync(ct);
        return Classify(rows.Select(r => (r.Id, r.Name)).ToList(), needle);
    }

    private static string Needle(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    private static ReferenceResult None_() => new(ReferenceStatus.NotFound, null, None);

    /// <summary>
    /// Exact name first. Several exact matches are Multiple and never fall through to partial matching.
    /// Partial matches are only used when no exact match exists, and a single partial match is still marked Partial.
    /// </summary>
    private static ReferenceResult Classify(List<(int Id, string Name)> candidates, string needle)
    {
        var exact = candidates.Where(c => c.Name.Trim().ToLowerInvariant() == needle).Select(c => c.Id).Take(MaxMatches).ToList();
        if (exact.Count > 0) return Single(exact, MatchKind.Exact);

        var partial = candidates.Select(c => c.Id).Take(MaxMatches).ToList();
        return partial.Count == 0 ? None_() : Single(partial, MatchKind.Partial);
    }

    private static ReferenceResult Single(List<int> ids, MatchKind kind) =>
        ids.Count == 1 ? new(ReferenceStatus.Resolved, ids[0], ids, kind) : new(ReferenceStatus.Multiple, null, ids, kind);
}
