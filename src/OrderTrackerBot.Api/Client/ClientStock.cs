using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>Sets the stock count of one tracked product. A product with no stock number is not tracked and is refused.</summary>
public sealed class ClientStock(AppDbContext db)
{
    public const int MaxStock = 1_000_000;

    public async Task<(string? Problem, ProductStockDto? Value)> SetAsync(int sellerId, int productId, StockInput input, CancellationToken ct)
    {
        if (input.StockQty is not { } qty || qty < 0 || qty > MaxStock) return ($"Stock must be between 0 and {MaxStock:N0}.", null);
        var product = await db.Products.FirstOrDefaultAsync(p => p.SellerId == sellerId && p.Id == productId, ct);
        if (product is null) return ("Product not found.", null);
        if (product.StockQty is null) return ("This product has no stock number. Set one in the product form first.", null);
        product.StockQty = qty;
        await db.SaveChangesAsync(ct);
        return (null, new ProductStockDto(product.Id, product.Name, product.StockQty));
    }
}
