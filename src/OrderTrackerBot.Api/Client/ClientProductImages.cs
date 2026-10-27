using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

public sealed record ProductImageDto(int Id, string ContentType, int Bytes, DateTime CreatedAt);

/// <summary>
/// Product photos for the dashboard gallery. JPG, PNG and WebP only, judged by their first bytes (not the file name or
/// the uploaded content type), at most 2 MB each and 8 per product. Every query is scoped to the seller.
/// </summary>
public sealed class ClientProductImages(AppDbContext db)
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxPerProduct = 8;

    /// <summary>The image type from its first bytes, or null when it is not a supported picture.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "image/jpeg";
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return "image/png";
        if (head.Length >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
            head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P') return "image/webp";
        return null;
    }

    /// <summary>Saves a photo. Returns a problem in plain words, or null with the new image id.</summary>
    public async Task<(string? Problem, int Id)> AddAsync(int sellerId, int productId, byte[] data, CancellationToken ct)
    {
        if (data.Length == 0) return ("Choose a photo to upload.", 0);
        if (data.Length > MaxBytes) return ("Photos can be 2 MB at most.", 0);
        var contentType = DetectContentType(data);
        if (contentType is null) return ("Upload a JPG, PNG or WebP photo.", 0);

        var productExists = await db.Products.AnyAsync(p => p.SellerId == sellerId && p.Id == productId, ct);
        if (!productExists) return ("Product not found.", 0);
        var count = await db.ProductImages.CountAsync(i => i.SellerId == sellerId && i.ProductId == productId, ct);
        if (count >= MaxPerProduct) return ($"A product can have {MaxPerProduct} photos. Delete one first.", 0);

        var image = new ProductImage { SellerId = sellerId, ProductId = productId, ContentType = contentType, Data = data };
        db.ProductImages.Add(image);
        await db.SaveChangesAsync(ct);
        return (null, image.Id);
    }

    public Task<List<ProductImageDto>> ListAsync(int sellerId, int productId, CancellationToken ct) =>
        db.ProductImages.AsNoTracking()
            .Where(i => i.SellerId == sellerId && i.ProductId == productId)
            .OrderBy(i => i.Id)
            .Select(i => new ProductImageDto(i.Id, i.ContentType, i.Data.Length, i.CreatedAt))
            .ToListAsync(ct);

    public Task<ProductImage?> GetAsync(int sellerId, int productId, int imageId, CancellationToken ct) =>
        db.ProductImages.AsNoTracking().FirstOrDefaultAsync(i => i.SellerId == sellerId && i.ProductId == productId && i.Id == imageId, ct);

    public async Task<bool> DeleteAsync(int sellerId, int productId, int imageId, CancellationToken ct)
    {
        var image = await db.ProductImages.FirstOrDefaultAsync(i => i.SellerId == sellerId && i.ProductId == productId && i.Id == imageId, ct);
        if (image is null) return false;
        db.ProductImages.Remove(image);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
