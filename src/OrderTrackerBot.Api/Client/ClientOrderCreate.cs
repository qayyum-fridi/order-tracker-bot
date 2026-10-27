using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>
/// Records a sale typed in the dashboard, the same way the WhatsApp order flow saves one: pending, catalog prices (the
/// customer's price group when it has one), the order's footprint taken from stock, and one OrderCreated log for undo.
/// </summary>
public sealed class ClientOrderCreate(AppDbContext db, ClientQueries queries)
{
    public const int MaxLines = 50;
    public const int MaxQuantity = 999;

    public async Task<(string? Problem, OrderDetailDto? Value)> CreateAsync(int sellerId, NewOrderInput input, CancellationToken ct)
    {
        if (input.CustomerId is not > 0) return ("Choose a customer.", null);
        var lines = (input.Items ?? []).Where(l => l.ProductId is not null || l.Quantity is not null).ToList();
        if (lines.Count == 0) return ("Add at least one product.", null);
        if (lines.Count > MaxLines) return ($"Add at most {MaxLines} products to one order.", null);
        if (lines.Any(l => l.ProductId is not > 0)) return ("Choose a product on every line.", null);
        if (lines.Any(l => l.Quantity is not > 0 || l.Quantity > MaxQuantity)) return ($"Each quantity must be from 1 to {MaxQuantity}.", null);
        if (input.DeliveryCharge is < 0 or > 1_000_000) return ("Delivery charge must be between 0 and 1,000,000.", null);
        var method = ParseMethod(input.PaymentMethod);
        if (method is null) return ("Choose Cash on delivery, Manual or Gateway.", null);

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == input.CustomerId && c.SellerId == sellerId && c.DeletedAt == null, ct);
        if (customer is null) return ("Customer not found.", null);
        var seller = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sellerId, ct);
        if (seller is null) return ("Seller not found.", null);

        var productIds = lines.Select(l => l.ProductId!.Value).Distinct().ToList();
        var products = await db.Products.Where(p => p.SellerId == sellerId && productIds.Contains(p.Id)).ToListAsync(ct);
        var missing = productIds.Except(products.Select(p => p.Id)).ToList();
        if (missing.Count > 0) return ("A product on the list was not found.", null);
        if (products.Any(p => !p.IsActive)) return ("A product on the list is switched off. Switch it on or remove it.", null);

        var order = new Order
        {
            SellerId = sellerId, CustomerId = customer.Id, Status = OrderStatus.Pending, PaymentMethod = method.Value,
            SalesTaxRate = seller.SalesTaxRate, OrderSource = "dashboard", Notes = Clean(input.Notes),
        };
        foreach (var line in lines)
        {
            var product = products.First(p => p.Id == line.ProductId);
            var unitPrice = PriceFor(product, customer.PriceGroup);
            order.Items.Add(new OrderItem
            {
                ProductId = product.Id, ProductNameSnapshot = product.Name, UnitPrice = unitPrice, UnitCost = product.CostPrice, Quantity = line.Quantity!.Value,
            });
        }
        order.Subtotal = order.Items.Sum(i => i.UnitPrice * i.Quantity);

        var (discountProblem, discount) = await DiscountAmountAsync(sellerId, input.DiscountCode, order.Subtotal, ct);
        if (discountProblem is not null) return (discountProblem, null);
        order.DiscountCode = discount.Code;
        order.DiscountAmount = discount.Amount;
        order.DeliveryCharge = input.DeliveryCharge ?? seller.DefaultDeliveryCharge;
        order.Total = Math.Max(0, order.Subtotal - order.DiscountAmount) + order.DeliveryCharge;

        var paid = input.AmountPaid ?? 0;
        if (paid < 0 || paid > order.Total) return ("Amount paid must be between 0 and the order total.", null);
        if (paid > 0)
        {
            order.AmountPaid = paid;
            if (paid == order.Total)
            {
                order.PaymentStatus = PaymentStatus.Paid;
                order.PaidAt = DateTime.UtcNow;
            }
        }

        db.Orders.Add(order);
        ClientOrderStatus.ApplyStock(products, new Dictionary<int, int>(), ClientOrderStatus.Footprint(order));
        await db.SaveChangesAsync(ct);

        // The undo entry needs the order id, which exists only after the save.
        db.ActionLogs.Add(new ActionLog
        {
            SellerId = sellerId, ActionType = ActionType.OrderCreated, OrderId = order.Id, PayloadJson = "{\"Source\":\"dashboard\"}",
        });
        await db.SaveChangesAsync(ct);
        return (null, await queries.OrderAsync(sellerId, order.Id, ct));
    }

    /// <summary>The customer's price group when the product has one for it; otherwise the regular price.</summary>
    private static decimal PriceFor(Product product, string? priceGroup) => priceGroup?.ToUpperInvariant() switch
    {
        "A" => product.PriceA ?? product.Price,
        "B" => product.PriceB ?? product.Price,
        "C" => product.PriceC ?? product.Price,
        _ => product.Price,
    };

    private static OrderPaymentMethod? ParseMethod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return OrderPaymentMethod.Cod;
        return Enum.TryParse<OrderPaymentMethod>(value, ignoreCase: true, out var method) && method is OrderPaymentMethod.Cod or OrderPaymentMethod.Manual or OrderPaymentMethod.Gateway
            ? method
            : null;
    }

    /// <summary>A code works when it is active and not expired. Percent rounds to paisa; a flat amount never exceeds the subtotal.</summary>
    private async Task<(string? Problem, (string? Code, decimal Amount) Value)> DiscountAmountAsync(int sellerId, string? codeInput, decimal subtotal, CancellationToken ct)
    {
        var code = codeInput?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) return (null, (null, 0));
        var discount = await db.Discounts.AsNoTracking().FirstOrDefaultAsync(d => d.SellerId == sellerId && d.Code.ToUpper() == code, ct);
        if (discount is null) return ($"Discount code {code} was not found.", (null, 0));
        if (!discount.IsActive || (discount.ExpiresAt is { } end && end <= DateTime.UtcNow)) return ($"Discount code {code} is not active.", (null, 0));
        var amount = discount.Type == DiscountType.Percent
            ? Math.Round(subtotal * discount.Value / 100, 2, MidpointRounding.AwayFromZero)
            : Math.Min(discount.Value, subtotal);
        return (null, (amount > 0 ? discount.Code : null, amount));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
