using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Daily sales import from the dashboard: one spreadsheet row per item, saved as orders. Products must already be in the
/// catalog (a row for an unknown product is skipped and reported). Customers are matched by phone, then by name; new ones
/// are created. Each order takes its items' stock like a WhatsApp order, and one "undo" cancels the whole import.
/// </summary>
public partial class ConversationEngine
{
    private sealed record ResolvedSalesLine(int Row, Product Product, int Quantity, decimal UnitPrice);

    /// <summary>An order ready to save. The customer is an existing one, or a new one shared by this file's orders.</summary>
    private sealed record ResolvedSalesOrder(
        DateOnly Day, Customer Customer, string? Phone, string Payment, string Status, List<ResolvedSalesLine> Lines)
    {
        public decimal Total => Lines.Sum(l => l.UnitPrice * l.Quantity);
    }

    private sealed record SalesPlan(string? Problem, IReadOnlyList<ClientImportColumn> Mapped, IReadOnlyList<string> Ignored,
        int RowCount, List<ResolvedSalesOrder> Orders, List<Customer> NewCustomers, List<string> Errors);

    private async Task<ClientImportPreview> PreviewSalesImportAsync(int sellerId, byte[] bytes, string fileName, CancellationToken ct)
    {
        var plan = await PlanSalesImportAsync(sellerId, bytes, fileName, ct);
        if (plan.Problem is not null) return new ClientImportPreview(plan.Problem, [], [], 0, 0, 0, [], []);

        var sample = plan.Orders.Take(5).Select(o => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Date"] = o.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["Customer"] = o.Customer.Name,
            ["Items"] = string.Join(", ", o.Lines.Select(l => $"{l.Product.Name} × {l.Quantity}")),
            ["Total"] = o.Total.ToString("0.##", CultureInfo.InvariantCulture),
        }).ToList();
        return new ClientImportPreview(null, plan.Mapped, plan.Ignored, plan.RowCount, plan.Orders.Count, 0, plan.Errors, sample);
    }

    private async Task<ClientImportResult?> CommitSalesImportAsync(int sellerId, byte[] bytes, string fileName, CancellationToken ct)
    {
        var seller = await _db.Sellers.FirstAsync(s => s.Id == sellerId, ct);
        var plan = await PlanSalesImportAsync(sellerId, bytes, fileName, ct);
        if (plan.Problem is not null || plan.Orders.Count == 0) return null;

        _db.Customers.AddRange(plan.NewCustomers);
        var saved = new List<Order>();
        foreach (var order in plan.Orders)
        {
            var status = Enum.Parse<OrderStatus>(order.Status);
            var paymentMethod = Enum.Parse<OrderPaymentMethod>(order.Payment);
            var createdAt = ImportDayToUtc(seller, order.Day);
            // Bank, wallet and gateway sales count as paid; cash on delivery counts as paid once it was delivered.
            var paid = paymentMethod != OrderPaymentMethod.Cod || status == OrderStatus.Delivered;
            var total = order.Total;

            var entity = new Order
            {
                SellerId = seller.Id, Customer = order.Customer, Status = status, PaymentMethod = paymentMethod,
                PaymentStatus = paid ? PaymentStatus.Paid : PaymentStatus.Unpaid, OrderSource = "import",
                SalesTaxRate = seller.SalesTaxRate, Subtotal = total, DiscountAmount = 0, DeliveryCharge = 0, Total = total,
                CreatedAt = createdAt, UpdatedAt = createdAt,
            };
            if (paid)
            {
                entity.AmountPaid = total;
                entity.PaidAt = createdAt;
            }
            if (status is OrderStatus.Shipped or OrderStatus.Delivered) entity.ShippedAt = createdAt;
            if (status == OrderStatus.Delivered) entity.DeliveredAt = createdAt;
            foreach (var line in order.Lines)
            {
                entity.Items.Add(new OrderItem
                {
                    ProductId = line.Product.Id, ProductNameSnapshot = line.Product.Name, UnitPrice = line.UnitPrice,
                    UnitCost = line.Product.CostPrice, Quantity = line.Quantity,
                });
            }
            _db.Orders.Add(entity);
            saved.Add(entity);
        }
        await _db.SaveChangesAsync(ct);

        foreach (var order in saved)
            await ApplyStockChangeAsync(seller, new Dictionary<int, int>(), StockFootprint(order), ct);
        _stockWarnings.Clear();

        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.DataImported,
            PayloadJson = JsonSerializer.Serialize(new
            {
                CustomerIds = plan.NewCustomers.Select(c => c.Id).ToList(),
                ProductIds = Array.Empty<int>(),
                OrderIds = saved.Select(o => o.Id).ToList(),
            }),
        });
        await _db.SaveChangesAsync(ct);
        return new ClientImportResult(saved.Count, plan.Errors.Count);
    }

    private async Task<SalesPlan> PlanSalesImportAsync(int sellerId, byte[] bytes, string fileName, CancellationToken ct)
    {
        if (bytes.Length > MaxImportBytes) return new SalesPlan("TooBig", [], [], 0, [], [], []);
        var grid = _importReader!.Read(bytes, fileName);
        if (grid is null) return new SalesPlan("Unreadable", [], [], 0, [], [], []);

        var seller = await _db.Sellers.AsNoTracking().FirstAsync(s => s.Id == sellerId, ct);
        var today = DateOnly.FromDateTime(ImportTodayInSellerZone(seller));
        var parsed = SalesImportPlanner.Parse(grid, today);
        if (parsed.Problem is not null) return new SalesPlan(parsed.Problem, parsed.MappedColumns, parsed.IgnoredHeadings, parsed.RowCount, [], [], []);

        var errors = parsed.Errors.Select(e => $"row {e.Row}: {e.Message}").ToList();
        var products = await _db.Products.Where(p => p.SellerId == sellerId && p.IsActive).ToListAsync(ct);
        var productByName = products.GroupBy(p => p.Name.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var customers = await _db.Customers.Where(c => c.SellerId == sellerId && c.DeletedAt == null).ToListAsync(ct);
        var customerByPhone = customers.Where(c => c.Phone is not null).GroupBy(c => Last10(c.Phone!)).ToDictionary(g => g.Key, g => g.First());
        var customerByName = customers.GroupBy(c => c.Name.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var newCustomers = new List<Customer>();
        var newByKey = new Dictionary<string, Customer>();

        var orders = new List<ResolvedSalesOrder>();
        foreach (var order in parsed.Orders)
        {
            var lines = new List<ResolvedSalesLine>();
            foreach (var line in order.Lines)
            {
                if (!productByName.TryGetValue(line.Product.Trim().ToLowerInvariant(), out var product))
                {
                    errors.Add($"row {line.Row}: product \"{line.Product}\" is not in the catalog. Add it first (Products or Catalog import).");
                    continue;
                }
                lines.Add(new ResolvedSalesLine(line.Row, product, line.Quantity, line.Price ?? product.Price));
            }
            if (lines.Count == 0) continue;

            var customer = FindOrCreateCustomer(sellerId, order, customerByPhone, customerByName, newCustomers, newByKey);
            orders.Add(new ResolvedSalesOrder(order.Day, customer, order.Phone, order.Payment, order.Status, lines));
        }
        return new SalesPlan(null, parsed.MappedColumns, parsed.IgnoredHeadings, parsed.RowCount, orders, newCustomers, errors);
    }

    /// <summary>Same phone (last 10 digits) or, without a phone, the same name is the same customer. New ones are shared across the file's orders.</summary>
    private static Customer FindOrCreateCustomer(int sellerId, SalesImportOrder order,
        Dictionary<string, Customer> customerByPhone, Dictionary<string, Customer> customerByName,
        List<Customer> newCustomers, Dictionary<string, Customer> newByKey)
    {
        if (order.Phone is not null && customerByPhone.TryGetValue(Last10(order.Phone), out var byPhone)) return byPhone;
        if (order.Phone is null && order.CustomerName is not null && customerByName.TryGetValue(order.CustomerName.Trim().ToLowerInvariant(), out var byName)) return byName;

        var key = order.Phone is not null ? "p:" + Last10(order.Phone) : "n:" + order.CustomerName!.Trim().ToLowerInvariant();
        if (newByKey.TryGetValue(key, out var created)) return created;
        created = new Customer { SellerId = sellerId, Name = order.CustomerName ?? order.Phone!, Phone = order.Phone };
        newByKey[key] = created;
        newCustomers.Add(created);
        return created;
    }

    /// <summary>A day's sales are placed at noon in the seller's time zone, so the day is right wherever it is read.</summary>
    private static DateTime ImportDayToUtc(Seller seller, DateOnly day)
    {
        var local = day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, SellerClock.Resolve(seller.TimeZoneId));
    }

    private static DateTime ImportTodayInSellerZone(Seller seller) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SellerClock.Resolve(seller.TimeZoneId));
}
