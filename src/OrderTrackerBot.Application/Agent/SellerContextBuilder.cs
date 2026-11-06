using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Agent;

/// <summary>A catalog product the message may mean. <see cref="Score"/> is 0-1 (1 = every word of the name is in the message).</summary>
public sealed record ProductHit(int Id, string Name, decimal Price, int? Stock, double Score);

/// <summary>A saved customer the message may mean, with what this seller usually sells to them.</summary>
public sealed record CustomerHit(int Id, string Name, string? City, int Orders, DateTime? LastOrderAt, IReadOnlyList<string> UsualItems, double Score);

/// <summary>
/// What the AI is told about one seller before it plans: facts from the database, ranked for the message. The model proposes; this only supplies
/// the facts it needs to resolve "Polo" or "Sara" to a real entity. Nothing here is ever copied into a slot by the model (the verifier checks ids).
/// </summary>
public sealed class SellerContext
{
    public string? ShopType { get; init; }
    public IReadOnlyList<ProductHit> Products { get; init; } = Array.Empty<ProductHit>();
    public IReadOnlyList<CustomerHit> Customers { get; init; } = Array.Empty<CustomerHit>();
    /// <summary>The last actions the seller did, newest first ("OrderCreated #41", "ProductPriceChanged").</summary>
    public IReadOnlyList<string> RecentActions { get; init; } = Array.Empty<string>();
    public int PendingOrders { get; init; }
    /// <summary>The delivery charge this seller used most often in their last 20 orders (null when no order or no clear habit).</summary>
    public decimal? UsualDeliveryCharge { get; init; }
    public string? UsualPaymentMethod { get; init; }

    /// <summary>Compact text for the model prompt.</summary>
    public string Render()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(ShopType)) lines.Add($"Kind of shop: {ShopType}");
        if (Products.Count > 0)
            lines.Add("Catalog (best match first): " + string.Join("; ", Products.Select(p =>
                $"#{p.Id} {p.Name} Rs.{p.Price:0.##}" + (p.Stock is { } s ? $" stock {s}" : ""))));
        if (Customers.Count > 0)
            lines.Add("Customers (best match first): " + string.Join("; ", Customers.Select(c =>
                $"#{c.Id} {c.Name}" + (c.City is { Length: > 0 } ? $" ({c.City})" : "") + $", {c.Orders} orders" +
                (c.UsualItems.Count > 0 ? $", usually {string.Join("/", c.UsualItems)}" : ""))));
        if (RecentActions.Count > 0) lines.Add("Last actions (newest first): " + string.Join(", ", RecentActions));
        if (PendingOrders > 0) lines.Add($"Pending orders: {PendingOrders}");
        if (UsualDeliveryCharge is { } d) lines.Add($"Usual delivery charge: Rs.{d:0.##}");
        if (!string.IsNullOrWhiteSpace(UsualPaymentMethod)) lines.Add($"Usual payment method: {UsualPaymentMethod}");
        return string.Join("\n", lines);
    }
}

/// <summary>Builds a <see cref="SellerContext"/> with plain queries and scoring (no AI). Every list is capped so the prompt stays small.</summary>
public sealed class SellerContextBuilder
{
    public const int MaxProducts = 15;
    public const int MaxCustomers = 5;
    public const int MaxActions = 5;
    private const int ProductPool = 300;
    private const int CustomerPool = 300;

    private readonly IAppDbContext _db;

    public SellerContextBuilder(IAppDbContext db) => _db = db;

    public async Task<SellerContext> BuildAsync(Seller seller, string message, CancellationToken ct = default)
    {
        var words = Words(message);

        var products = await _db.Products.AsNoTracking()
            .Where(p => p.SellerId == seller.Id && p.IsActive)
            .OrderByDescending(p => p.Id).Take(ProductPool)
            .Select(p => new { p.Id, p.Name, p.Price, p.StockQty }).ToListAsync(ct);
        var productHits = products
            .Select(p => new ProductHit(p.Id, p.Name, p.Price, p.StockQty, MatchScore(words, p.Name)))
            .OrderByDescending(h => h.Score).ThenByDescending(h => h.Id)   // ties: the newer product
            .Take(MaxProducts).ToList();

        var customers = await _db.Customers.AsNoTracking()
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null)
            .OrderByDescending(c => c.Id).Take(CustomerPool)
            .Select(c => new { c.Id, c.Name, c.City }).ToListAsync(ct);
        var ranked = customers
            .Select(c => (c.Id, c.Name, c.City, Score: MatchScore(words, c.Name)))
            .Where(c => c.Score > 0)                                         // a customer is only listed when the message names them
            .OrderByDescending(c => c.Score).Take(MaxCustomers).ToList();
        var customerHits = new List<CustomerHit>();
        foreach (var c in ranked)
            customerHits.Add(await CustomerHitAsync(seller.Id, c.Id, c.Name, c.City, c.Score, ct));

        var actions = (await _db.ActionLogs.AsNoTracking()
                .Where(a => a.SellerId == seller.Id && !a.Undone)
                .OrderByDescending(a => a.Id).Take(MaxActions)
                .Select(a => new { a.ActionType, a.OrderId }).ToListAsync(ct))
            .Select(a => a.OrderId is { } o ? $"{a.ActionType} #{o}" : a.ActionType.ToString()).ToList();

        var pending = await _db.Orders.CountAsync(o => o.SellerId == seller.Id && o.Status == OrderStatus.Pending, ct);

        var recent = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .OrderByDescending(o => o.Id).Take(20)
            .Select(o => new { o.DeliveryCharge, o.PaymentMethod }).ToListAsync(ct);

        return new SellerContext
        {
            ShopType = seller.BusinessType,
            Products = productHits,
            Customers = customerHits,
            RecentActions = actions,
            PendingOrders = pending,
            UsualDeliveryCharge = MajorityOrNull(recent.Select(o => (decimal?)o.DeliveryCharge)),
            UsualPaymentMethod = MajorityOrNull(recent.Select(o => (OrderPaymentMethod?)o.PaymentMethod))?.ToString(),
        };
    }

    private async Task<CustomerHit> CustomerHitAsync(int sellerId, int customerId, string name, string? city, double score, CancellationToken ct)
    {
        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == sellerId && o.CustomerId == customerId && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .OrderByDescending(o => o.Id).Take(3)
            .Select(o => new { o.Id, o.CreatedAt }).ToListAsync(ct);
        var total = await _db.Orders.CountAsync(o => o.SellerId == sellerId && o.CustomerId == customerId, ct);
        var ids = orders.Select(o => o.Id).ToList();
        var usual = ids.Count == 0
            ? new List<string>()
            : (await _db.OrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId))
                    .Select(i => i.ProductNameSnapshot).ToListAsync(ct))
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(3).Select(g => g.Key).ToList();
        return new CustomerHit(customerId, name, city, total, orders.FirstOrDefault()?.CreatedAt, usual, score);
    }

    /// <summary>The value used in more than half of the orders, or null when there is no clear habit.</summary>
    private static T? MajorityOrNull<T>(IEnumerable<T?> values) where T : struct
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (list.Count < 3) return null;
        var top = list.GroupBy(v => v).OrderByDescending(g => g.Count()).First();
        return top.Count() * 2 > list.Count ? top.Key : null;
    }

    private static List<string> Words(string message) =>
        message.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', ';', ':', '-', '(', ')', '\n', '\t', '?', '!', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2).Distinct().ToList();

    /// <summary>
    /// 0-1: the share of the name's words found in the message, a word counting fully when it is equal and 0.7 when it is one edit away
    /// (a typo of 4+ letters). Names of one or two letters never match by typo.
    /// </summary>
    internal static double MatchScore(IReadOnlyList<string> messageWords, string name)
    {
        var nameWords = Words(name);
        if (nameWords.Count == 0 || messageWords.Count == 0) return 0;
        double sum = 0;
        foreach (var n in nameWords)
        {
            if (messageWords.Contains(n)) { sum += 1; continue; }
            if (n.Length >= 4 && messageWords.Any(m => m.Length >= 4 && TextDistance.Levenshtein(m, n) <= 1)) sum += 0.7;
        }
        return Math.Round(sum / nameWords.Count, 3);
    }
}
