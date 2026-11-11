using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Category sales: "create discount: WINTER20, 20 percent, category Winter 26" gives every item of that category 20% off.
// When an order has no code (or an unknown one), the best running category sale that matches its items applies by itself
// and the order carries that code. A typed normal code wins over category sales (no stacking).
public partial class ConversationEngine
{
    /// <summary>Product category matches the sale category: the same name, or a sub-category of it ("Winter 26 &gt; Khaddar").</summary>
    internal static bool InCategory(string? productCategory, string saleCategory)
    {
        var category = productCategory?.Trim();
        if (string.IsNullOrEmpty(category)) return false;
        return string.Equals(category, saleCategory, StringComparison.OrdinalIgnoreCase)
            || category.StartsWith(saleCategory + " >", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sum of the line amounts whose product (by id) sits in the sale category. Lines without a catalog product never match.</summary>
    internal static decimal CategoryBase(IEnumerable<(int? ProductId, decimal Amount)> lines, IReadOnlyDictionary<int, string?> productCategories, string saleCategory) =>
        lines.Where(l => l.ProductId is int id && productCategories.TryGetValue(id, out var category) && InCategory(category, saleCategory))
            .Sum(l => l.Amount);

    private async Task<IReadOnlyDictionary<int, string?>> ProductCategoriesAsync(Seller seller, IEnumerable<int?> productIds, CancellationToken ct)
    {
        var ids = productIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, string?>();
        return await _db.Products
            .Where(p => p.SellerId == seller.Id && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Category, ct);
    }

    /// <summary>The running category sale that takes the most off these lines, with its amount; null when none matches.</summary>
    private async Task<(Discount? Sale, decimal Amount)> BestCategorySaleAsync(Seller seller, IReadOnlyList<(int? ProductId, decimal Amount)> lines, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sales = await _db.Discounts
            .Where(d => d.SellerId == seller.Id && d.IsActive && d.Category != null && d.Type == DiscountType.Percent
                && (d.ExpiresAt == null || d.ExpiresAt > now))
            .ToListAsync(ct);
        if (sales.Count == 0) return (null, 0);

        var categories = await ProductCategoriesAsync(seller, lines.Select(l => l.ProductId), ct);
        Discount? best = null;
        decimal bestAmount = 0;
        foreach (var sale in sales)
        {
            var amount = Math.Round(CategoryBase(lines, categories, sale.Category!) * sale.Value / 100m, 2);
            if (amount > bestAmount) { best = sale; bestAmount = amount; }
        }
        return (best, bestAmount);
    }
}
