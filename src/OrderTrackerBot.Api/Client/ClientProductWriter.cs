using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>
/// Product fields the dashboard sends. PUT replaces the product, so a null optional field clears it.
/// Money is in rupees; stock null = stock not tracked.
/// </summary>
public sealed record ClientProductInput(
    string? Name, decimal? Price, decimal? PriceA, decimal? PriceB, decimal? PriceC, string? UnitType, decimal? UnitQty,
    string? Category, string? Size, string? Color, string? Sku, int? StockQty, decimal? CostPrice, bool? IsActive,
    string? DefaultPriceGroup, string? ProductType);

/// <summary>Outcome of a product write. Status is the HTTP status to return; Id is set when the product was saved.</summary>
public sealed record ProductWriteResult(int? Id, int Status, string? Error);

/// <summary>
/// Creates, replaces and deactivates a seller's products from the dashboard. The same product rules as the WhatsApp
/// product lines apply: a name, non-negative prices, one SKU per seller (case-insensitive), and an automatic SKU when
/// none is typed.
/// </summary>
public sealed class ClientProductWriter(AppDbContext db)
{
    private const decimal MaxMoney = 100_000_000m;
    private const int MaxStock = 1_000_000;
    private const int MaxText = 60;
    private static readonly string[] PriceGroups = ["A", "B", "C"];
    private static readonly string[] ProductTypes = ["sale", "service"];

    public async Task<ProductWriteResult> CreateAsync(int sellerId, ClientProductInput input, CancellationToken ct)
    {
        var error = Validate(input);
        if (error is not null) return new ProductWriteResult(null, 400, error);

        var sku = Clean(input.Sku);
        if (sku is null) sku = await NewSkuAsync(sellerId, ct);
        else if (await SkuTakenAsync(sellerId, sku, null, ct)) return new ProductWriteResult(null, 409, "Another product already uses this SKU.");

        var product = new Product
        {
            SellerId = sellerId,
            Name = input.Name!.Trim(),
            IsActive = input.IsActive ?? true,
            CreatedAt = DateTime.UtcNow,
        };
        Apply(product, input, sku);
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return new ProductWriteResult(product.Id, 200, null);
    }

    public async Task<ProductWriteResult> UpdateAsync(int sellerId, int id, ClientProductInput input, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id && p.SellerId == sellerId, ct);
        if (product is null) return new ProductWriteResult(null, 404, "Product not found.");

        var error = Validate(input);
        if (error is not null) return new ProductWriteResult(null, 400, error);

        var sku = Clean(input.Sku);
        if (sku is null) sku = product.Sku ?? await NewSkuAsync(sellerId, ct);
        else if (await SkuTakenAsync(sellerId, sku, id, ct)) return new ProductWriteResult(null, 409, "Another product already uses this SKU.");

        product.Name = input.Name!.Trim();
        product.IsActive = input.IsActive ?? product.IsActive;
        Apply(product, input, sku);
        await db.SaveChangesAsync(ct);
        return new ProductWriteResult(product.Id, 200, null);
    }

    /// <summary>Hides the product from the catalog and sales lists. Past orders keep their item names and prices.</summary>
    public async Task<ProductWriteResult> DeactivateAsync(int sellerId, int id, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id && p.SellerId == sellerId, ct);
        if (product is null) return new ProductWriteResult(null, 404, "Product not found.");
        product.IsActive = false;
        await db.SaveChangesAsync(ct);
        return new ProductWriteResult(id, 200, null);
    }

    private static void Apply(Product product, ClientProductInput input, string? sku)
    {
        product.Price = Math.Round(input.Price!.Value, 2);
        product.PriceA = input.PriceA is { } a ? Math.Round(a, 2) : null;
        product.PriceB = input.PriceB is { } b ? Math.Round(b, 2) : null;
        product.PriceC = input.PriceC is { } c ? Math.Round(c, 2) : null;
        product.UnitType = Clean(input.UnitType)?.ToLowerInvariant() ?? "piece";
        product.UnitQty = input.UnitQty ?? 1m;
        product.Category = Clean(input.Category);
        product.Size = Clean(input.Size);
        product.Color = Clean(input.Color);
        product.Sku = sku;
        product.StockQty = input.StockQty;
        product.CostPrice = input.CostPrice is { } cost ? Math.Round(cost, 2) : null;
        product.DefaultPriceGroup = Clean(input.DefaultPriceGroup)?.ToUpperInvariant();
        product.ProductType = Clean(input.ProductType)?.ToLowerInvariant() ?? "sale";
    }

    /// <summary>The first problem found, in words a shop owner can act on. Null when the input is fine.</summary>
    private static string? Validate(ClientProductInput input)
    {
        var name = Clean(input.Name);
        if (name is null) return "Enter the product name.";
        if (name.Length > 120) return "The product name is too long (120 characters at most).";
        if (input.Price is null) return "Enter the selling price.";
        if (input.Price < 0 || input.Price > MaxMoney) return "The selling price must be between 0 and 100,000,000.";
        foreach (var level in new[] { input.PriceA, input.PriceB, input.PriceC, input.CostPrice })
            if (level is < 0 or > MaxMoney) return "Prices must be between 0 and 100,000,000.";
        if (input.UnitQty is <= 0) return "The quantity per unit must be more than zero.";
        if (input.StockQty is < 0 or > MaxStock) return "Stock must be between 0 and 1,000,000.";
        if (Clean(input.DefaultPriceGroup) is { } group && !PriceGroups.Contains(group.ToUpperInvariant()))
            return "The default price must be A, B or C, or left empty.";
        if (Clean(input.ProductType) is { } type && !ProductTypes.Contains(type.ToLowerInvariant()))
            return "The product type must be sale or service.";
        if (Clean(input.UnitType) is { Length: > 20 }) return "The unit name is too long (20 characters at most).";
        if (Clean(input.Sku) is { Length: > 40 }) return "The SKU is too long (40 characters at most).";
        foreach (var text in new[] { input.Category, input.Size, input.Color })
            if (Clean(text) is { Length: > MaxText }) return $"Keep each text field to {MaxText} characters or fewer.";
        return null;
    }

    private async Task<bool> SkuTakenAsync(int sellerId, string sku, int? exceptId, CancellationToken ct)
    {
        var lower = sku.ToLowerInvariant();
        return await db.Products.AnyAsync(p => p.SellerId == sellerId && p.Sku != null && p.Sku.ToLower() == lower && p.Id != exceptId, ct);
    }

    /// <summary>A new SKU of the form SKU-123456 that no product of this seller uses yet.</summary>
    private async Task<string> NewSkuAsync(int sellerId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var sku = $"SKU-{RandomNumberGenerator.GetInt32(100_000, 1_000_000)}";
            if (!await SkuTakenAsync(sellerId, sku, null, ct)) return sku;
        }
        throw new InvalidOperationException("Could not find a free SKU after 20 tries.");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
