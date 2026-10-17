using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>Renames a top-level category on every product that has it. Subcategories move with their product.</summary>
public sealed class ClientCategoryRename(AppDbContext db)
{
    public async Task<(string? Problem, int Updated)> RenameAsync(int sellerId, CategoryRenameInput input, CancellationToken ct)
    {
        var from = input.From?.Trim();
        var to = input.To?.Trim();
        if (string.IsNullOrEmpty(from)) return ("Choose the category to rename.", 0);
        if (string.IsNullOrEmpty(to)) return ("Enter the new category name.", 0);
        if (to.Length > 60) return ("Keep the category name to 60 characters.", 0);
        if (string.Equals(from, "Uncategorized", StringComparison.OrdinalIgnoreCase)) return ("Products without a category can't be renamed. Set their category in Products instead.", 0);
        if (string.Equals(from, to, StringComparison.Ordinal)) return ("The new name is the same as the old one.", 0);

        var products = await db.Products.Where(p => p.SellerId == sellerId && p.Category == from).ToListAsync(ct);
        foreach (var p in products) p.Category = to;
        await db.SaveChangesAsync(ct);
        return (null, products.Count);
    }
}

/// <summary>
/// CSV downloads for the dashboard: orders, order items, customers and catalog. Orders and items use the requested
/// Pakistan-day range; customers and catalog are the whole account. Cells are always quoted, a leading = + - @ is
/// neutralised so Excel cannot run it as a formula, and the file starts with a UTF-8 byte-order mark so Urdu text opens correctly.
/// </summary>
public sealed class ClientExport(AppDbContext db)
{
    public static readonly string[] Datasets = ["orders", "order-items", "customers", "catalog"];

    public async Task<(string FileName, string Content)> BuildAsync(int sellerId, string dataset, AdminDateRange range, CancellationToken ct)
    {
        var rows = dataset switch
        {
            "orders" => await OrdersAsync(sellerId, range, ct),
            "order-items" => await ItemsAsync(sellerId, range, ct),
            "customers" => await CustomersAsync(sellerId, ct),
            "catalog" => await CatalogAsync(sellerId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(dataset)),
        };
        var fileName = $"{dataset}_{range.FromDay.ToString(AdminDateRange.DateFormat)}_to_{range.ToDay.ToString(AdminDateRange.DateFormat)}.csv";
        return (fileName, "﻿" + string.Join("\r\n", rows.Select(r => string.Join(",", r.Select(Cell)))) + "\r\n");
    }

    private async Task<List<string[]>> OrdersAsync(int sellerId, AdminDateRange range, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking().Include(o => o.Customer)
            .Where(o => o.SellerId == sellerId && o.CreatedAt >= range.StartUtc && o.CreatedAt < range.EndUtc)
            .OrderBy(o => o.Id).ToListAsync(ct);
        var rows = new List<string[]> { new[] { "Order", "Date (PKT)", "Customer", "Status", "Payment method", "Payment status", "Items", "Discount", "Delivery", "Total", "Amount paid", "Receipt", "Courier", "Tracking number" } };
        rows.AddRange(orders.Select(o => new[]
        {
            o.Id.ToString(CultureInfo.InvariantCulture), Local(o.CreatedAt), o.Customer?.Name ?? "", o.Status.ToString(), o.PaymentMethod.ToString(), o.PaymentStatus.ToString(),
            Money(o.Subtotal), Money(o.DiscountAmount), Money(o.DeliveryCharge), Money(o.Total), Money(o.AmountPaid),
            o.ReceiptNumber?.ToString(CultureInfo.InvariantCulture) ?? "", o.TrackingCourier ?? "", o.TrackingNumber ?? "",
        }));
        return rows;
    }

    private async Task<List<string[]>> ItemsAsync(int sellerId, AdminDateRange range, CancellationToken ct)
    {
        var items = await db.OrderItems.AsNoTracking().Include(i => i.Order)
            .Where(i => i.Order!.SellerId == sellerId && i.Order.CreatedAt >= range.StartUtc && i.Order.CreatedAt < range.EndUtc)
            .OrderBy(i => i.OrderId).ThenBy(i => i.Id).ToListAsync(ct);
        var rows = new List<string[]> { new[] { "Order", "Date (PKT)", "Product", "Unit price", "Unit cost", "Quantity", "Line total" } };
        rows.AddRange(items.Select(i => new[]
        {
            i.OrderId.ToString(CultureInfo.InvariantCulture), Local(i.Order!.CreatedAt), i.ProductNameSnapshot,
            Money(i.UnitPrice), i.UnitCost.HasValue ? Money(i.UnitCost.Value) : "", i.Quantity.ToString(CultureInfo.InvariantCulture), Money(i.UnitPrice * i.Quantity),
        }));
        return rows;
    }

    private async Task<List<string[]>> CustomersAsync(int sellerId, CancellationToken ct)
    {
        var customers = await db.Customers.AsNoTracking().Where(c => c.SellerId == sellerId && c.DeletedAt == null).OrderBy(c => c.Id).ToListAsync(ct);
        var sales = await db.Orders.AsNoTracking()
            .Where(o => o.SellerId == sellerId && o.Status != Domain.Enums.OrderStatus.Cancelled && o.Status != Domain.Enums.OrderStatus.Returned)
            .Select(o => new { o.CustomerId, o.Total, o.CreatedAt }).ToListAsync(ct);
        var byCustomer = sales.GroupBy(s => s.CustomerId).ToDictionary(g => g.Key, g => (Count: g.Count(), Spent: g.Sum(x => x.Total), Last: g.Max(x => x.CreatedAt)));

        var rows = new List<string[]> { new[] { "Customer", "Name", "Phone", "City", "Address", "Preferred contact", "Price group", "Orders", "Total spent", "Last order (PKT)" } };
        rows.AddRange(customers.Select(c =>
        {
            byCustomer.TryGetValue(c.Id, out var stat);
            return new[]
            {
                c.Id.ToString(CultureInfo.InvariantCulture), c.Name, c.Phone ?? "", c.City ?? "", c.Address ?? "", c.PreferredContact ?? "", c.PriceGroup ?? "",
                stat.Count.ToString(CultureInfo.InvariantCulture), Money(stat.Spent), stat.Count > 0 ? Local(stat.Last) : "",
            };
        }));
        return rows;
    }

    private async Task<List<string[]>> CatalogAsync(int sellerId, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.SellerId == sellerId).OrderBy(p => p.Id).ToListAsync(ct);
        var rows = new List<string[]> { new[] { "Product", "Name", "Category", "Subcategory", "SKU", "Price", "Cost price", "Unit", "Stock", "Active" } };
        rows.AddRange(products.Select(p => new[]
        {
            p.Id.ToString(CultureInfo.InvariantCulture), p.Name, p.Category ?? "", ClientQueries.SubcategoryOf(p.AttributesJson) ?? "", p.Sku ?? "",
            Money(p.Price), p.CostPrice.HasValue ? Money(p.CostPrice.Value) : "", p.UnitType, p.StockQty?.ToString(CultureInfo.InvariantCulture) ?? "",
            p.IsActive ? "Yes" : "No",
        }));
        return rows;
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), AdminDateRange.SellerZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Quoted, with quotes doubled. Values that Excel would read as a formula get a leading apostrophe.</summary>
    private static string Cell(string value)
    {
        var safe = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }
}
