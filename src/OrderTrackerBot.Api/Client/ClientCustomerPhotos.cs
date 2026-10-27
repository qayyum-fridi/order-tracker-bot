using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>
/// One photo per customer, for the dashboard. JPG, PNG or WebP judged by the first bytes, at most 2 MB. A new upload
/// replaces the old photo. Every query is scoped to the seller.
/// </summary>
public sealed class ClientCustomerPhotos(AppDbContext db)
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static string? Validate(byte[] data) =>
        data.Length == 0 ? "Choose a photo to upload."
        : data.Length > MaxBytes ? "Photos can be 2 MB at most."
        : ClientProductImages.DetectContentType(data) is null ? "Upload a JPG, PNG or WebP photo."
        : null;

    public async Task<string?> SetAsync(int sellerId, int customerId, byte[] data, CancellationToken ct)
    {
        var problem = Validate(data);
        if (problem is not null) return problem;
        var customerExists = await db.Customers.AnyAsync(c => c.SellerId == sellerId && c.Id == customerId && c.DeletedAt == null, ct);
        if (!customerExists) return "Customer not found.";

        var contentType = ClientProductImages.DetectContentType(data)!;
        var existing = await db.CustomerPhotos.FirstOrDefaultAsync(p => p.SellerId == sellerId && p.CustomerId == customerId, ct);
        if (existing is null)
            db.CustomerPhotos.Add(new CustomerPhoto { SellerId = sellerId, CustomerId = customerId, ContentType = contentType, Data = data });
        else
        {
            existing.ContentType = contentType;
            existing.Data = data;
            existing.CreatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return null;
    }

    public Task<CustomerPhoto?> GetAsync(int sellerId, int customerId, CancellationToken ct) =>
        db.CustomerPhotos.AsNoTracking().FirstOrDefaultAsync(p => p.SellerId == sellerId && p.CustomerId == customerId, ct);

    public async Task<bool> DeleteAsync(int sellerId, int customerId, CancellationToken ct)
    {
        var photo = await db.CustomerPhotos.FirstOrDefaultAsync(p => p.SellerId == sellerId && p.CustomerId == customerId, ct);
        if (photo is null) return false;
        db.CustomerPhotos.Remove(photo);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
