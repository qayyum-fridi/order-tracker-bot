using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>
/// One order's status change from the dashboard. The allowed moves are the dashboard's order state machine
/// (Pending to Shipped or Cancelled, Shipped to Delivered or Returned, Delivered to Returned). Stock follows the
/// order's footprint and the change is logged once so "undo" can revert it.
/// </summary>
public sealed class ClientOrderStatus(AppDbContext db, ClientQueries queries)
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Next = new()
    {
        [OrderStatus.Pending] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered, OrderStatus.Returned],
        [OrderStatus.Delivered] = [OrderStatus.Returned],
    };

    /// <summary>Stock quantities an order holds: its items, except cancelled and returned orders hold nothing.</summary>
    public static Dictionary<int, int> Footprint(Order order) =>
        order.Status is OrderStatus.Cancelled or OrderStatus.Returned
            ? new Dictionary<int, int>()
            : order.Items.Where(i => i.ProductId is not null).GroupBy(i => i.ProductId!.Value).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

    /// <summary>Moves stock by the difference between the two footprints. Untracked products (no stock number) are left alone.</summary>
    public static void ApplyStock(IEnumerable<Product> products, Dictionary<int, int> before, Dictionary<int, int> after)
    {
        var byId = products.ToDictionary(p => p.Id);
        foreach (var productId in before.Keys.Union(after.Keys))
        {
            var delta = after.GetValueOrDefault(productId) - before.GetValueOrDefault(productId);
            if (delta == 0) continue;
            if (byId.TryGetValue(productId, out var product) && product.StockQty is not null) product.StockQty -= delta;
        }
    }

    public static bool CanMove(OrderStatus from, OrderStatus to) => Next.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static (string? Problem, OrderStatus Target) Parse(string? status) =>
        Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var target) && target is not OrderStatus.Pending
            ? (null, target)
            : ("Choose Shipped, Delivered, Cancelled or Returned.", default);

    /// <summary>Returns a problem in plain words, or null with the order's new detail.</summary>
    public async Task<(string? Problem, OrderDetailDto? Value)> ChangeAsync(int sellerId, int orderId, string? status, CancellationToken ct)
    {
        var (parseProblem, target) = Parse(status);
        if (parseProblem is not null) return (parseProblem, null);

        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.SellerId == sellerId && o.Id == orderId, ct);
        if (order is null) return ("Order not found.", null);
        var previous = order.Status;
        if (!CanMove(previous, target)) return ($"An order cannot go from {previous} to {target}.", null);

        var products = await db.Products.Where(p => p.SellerId == sellerId && p.StockQty != null).ToListAsync(ct);
        var before = Footprint(order);
        order.Status = target;
        ApplyStock(products, before, Footprint(order));

        var now = DateTime.UtcNow;
        if (target == OrderStatus.Shipped) order.ShippedAt = now;
        if (target == OrderStatus.Delivered) order.DeliveredAt = now;
        if (target == OrderStatus.Cancelled) order.CancelledAt = now;
        if (target == OrderStatus.Returned) order.ReturnedAt = now;

        db.ActionLogs.Add(new ActionLog
        {
            SellerId = sellerId,
            ActionType = ActionType.OrderStatusChanged,
            OrderId = order.Id,
            PayloadJson = JsonSerializer.Serialize(new { PreviousStatus = previous.ToString() }),
        });
        await db.SaveChangesAsync(ct);
        return (null, await queries.OrderAsync(sellerId, orderId, ct));
    }
}
