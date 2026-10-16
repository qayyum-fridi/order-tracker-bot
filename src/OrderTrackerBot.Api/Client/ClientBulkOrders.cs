using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>Body of a bulk order status change. Status is shipped, delivered or returned.</summary>
public sealed record ClientOrderBulkInput(IReadOnlyList<int>? Ids, string? Status);

/// <summary>Body of a bulk customer price-group change. PriceGroup is A, B or C; null or empty clears it.</summary>
public sealed record ClientCustomerBulkInput(IReadOnlyList<int>? Ids, string? PriceGroup);

/// <summary>Orders changed and orders left alone because the move is not allowed from their current status.</summary>
public sealed record ClientBulkResult(int Updated, int Skipped);

/// <summary>
/// Moves several of the seller's orders to shipped, delivered or returned. Same allowed moves as the WhatsApp
/// "mark N shipped/delivered/returned" command; anything else is skipped and counted. Stock follows the order's
/// footprint, as in the engine, and each change is logged once so "undo" can revert it.
/// </summary>
public sealed class ClientOrderBulk(AppDbContext db)
{
    public const int MaxItems = 200;
    private static readonly string[] Statuses = ["shipped", "delivered", "returned"];

    public static string? Validate(ClientOrderBulkInput input)
    {
        if (input.Ids is null || input.Ids.Count == 0) return "Select at least one order.";
        if (input.Ids.Count > MaxItems) return $"Change at most {MaxItems} orders at a time.";
        if (input.Ids.Any(id => id <= 0)) return "Order numbers must be positive.";
        if (input.Status is null || !Statuses.Contains(input.Status)) return "Choose shipped, delivered or returned.";
        return null;
    }

    public async Task<ClientBulkResult> BulkStatusAsync(int sellerId, ClientOrderBulkInput input, CancellationToken ct)
    {
        var ids = input.Ids!.Distinct().ToList();
        var target = input.Status switch
        {
            "shipped" => OrderStatus.Shipped,
            "delivered" => OrderStatus.Delivered,
            _ => OrderStatus.Returned,
        };
        var orders = await db.Orders.Include(o => o.Items)
            .Where(o => o.SellerId == sellerId && ids.Contains(o.Id)).ToListAsync(ct);
        var products = await db.Products.Where(p => p.SellerId == sellerId && p.StockQty != null).ToListAsync(ct);

        var updated = 0;
        foreach (var order in orders)
        {
            if (!Allowed(order.Status, target))
                continue;

            var previous = order.Status;
            var before = Footprint(order);
            order.Status = target;
            var after = Footprint(order);
            ApplyStock(products, before, after);

            var now = DateTime.UtcNow;
            if (target == OrderStatus.Shipped) order.ShippedAt = now;
            if (target == OrderStatus.Delivered) order.DeliveredAt = now;
            if (target == OrderStatus.Returned) order.ReturnedAt = now;

            db.ActionLogs.Add(new ActionLog
            {
                SellerId = sellerId,
                ActionType = ActionType.OrderStatusChanged,
                OrderId = order.Id,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { PreviousStatus = previous.ToString() }),
            });
            updated++;
        }

        await db.SaveChangesAsync(ct);
        return new ClientBulkResult(updated, ids.Count - updated);
    }

    /// <summary>Pending or shipped can move forward; shipped or delivered can come back. Cancelled and returned orders stay put.</summary>
    private static bool Allowed(OrderStatus from, OrderStatus to) => (to, from) switch
    {
        (OrderStatus.Shipped, OrderStatus.Pending) => true,
        (OrderStatus.Delivered, OrderStatus.Pending or OrderStatus.Shipped) => true,
        (OrderStatus.Returned, OrderStatus.Shipped or OrderStatus.Delivered) => true,
        _ => false,
    };

    private static Dictionary<int, int> Footprint(Order order) =>
        order.Status is OrderStatus.Cancelled or OrderStatus.Returned
            ? new Dictionary<int, int>()
            : order.Items.Where(i => i.ProductId is not null).GroupBy(i => i.ProductId!.Value).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

    private static void ApplyStock(List<Product> products, Dictionary<int, int> before, Dictionary<int, int> after)
    {
        foreach (var productId in before.Keys.Union(after.Keys))
        {
            var delta = after.GetValueOrDefault(productId) - before.GetValueOrDefault(productId);
            if (delta == 0) continue;
            var product = products.FirstOrDefault(p => p.Id == productId);
            if (product?.StockQty is not null) product.StockQty -= delta;
        }
    }
}

/// <summary>Sets the price group of several of the seller's customers at once. Deleted customers are ignored.</summary>
public sealed class ClientCustomerBulk(AppDbContext db)
{
    public const int MaxItems = 200;
    private static readonly string[] Groups = ["A", "B", "C"];

    public static string? Validate(ClientCustomerBulkInput input)
    {
        if (input.Ids is null || input.Ids.Count == 0) return "Select at least one customer.";
        if (input.Ids.Count > MaxItems) return $"Change at most {MaxItems} customers at a time.";
        if (input.Ids.Any(id => id <= 0)) return "Customer numbers must be positive.";
        var group = input.PriceGroup?.Trim();
        if (!string.IsNullOrEmpty(group) && !Groups.Contains(group.ToUpperInvariant())) return "Choose price group A, B or C, or leave it empty.";
        return null;
    }

    public async Task<int> BulkPriceGroupAsync(int sellerId, ClientCustomerBulkInput input, CancellationToken ct)
    {
        var ids = input.Ids!.Distinct().ToList();
        var group = input.PriceGroup?.Trim();
        var value = string.IsNullOrEmpty(group) ? null : group.ToUpperInvariant();
        var customers = await db.Customers.Where(c => c.SellerId == sellerId && c.DeletedAt == null && ids.Contains(c.Id)).ToListAsync(ct);
        foreach (var c in customers) c.PriceGroup = value;
        await db.SaveChangesAsync(ct);
        return customers.Count;
    }
}
