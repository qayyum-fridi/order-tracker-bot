using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>
/// Read-only queries behind the seller dashboard. Every method takes the seller id from the token, so one seller
/// can never see another's rows. Money is summed in memory where SQLite can't aggregate decimal.
/// </summary>
public sealed class ClientQueries(AppDbContext db)
{
    private const int MaxPageSize = 100;
    private const int MaxMessages = 200;
    private const int RecentOrderCount = 8;
    private const int LowStockThreshold = 5;
    private const int LowStockCount = 5;
    private const int TopProductCount = 10;
    private static readonly OrderStatus[] NotSales = [OrderStatus.Cancelled, OrderStatus.Returned];

    public async Task<ClientProfileDto?> ProfileAsync(int sellerId, CancellationToken ct)
    {
        var s = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sellerId, ct);
        return s is null ? null : new ClientProfileDto(
            s.Id, s.WhatsAppPhoneNumber, s.BusinessName, s.City, s.BusinessType, s.Email, s.TimeZoneId,
            s.OnboardingComplete, s.Plan.ToString(), s.Status.ToString(), Utc(s.TrialEndsAt),
            Utc(s.SubscriptionActiveUntil), Utc(s.CreatedAt));
    }

    public async Task<SetupDto?> SetupAsync(int sellerId, CancellationToken ct)
    {
        var s = await db.Sellers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sellerId, ct);
        if (s is null) return null;

        var hasProduct = await db.Products.AnyAsync(p => p.SellerId == sellerId && p.IsActive, ct);
        var hasPayment = await db.PaymentMethods.AnyAsync(m => m.SellerId == sellerId, ct);
        var hasOrder = await db.Orders.AnyAsync(o => o.SellerId == sellerId, ct);

        var steps = new List<SetupStepDto>
        {
            new("businessName", "Business name", !string.IsNullOrWhiteSpace(s.BusinessName), "/config"),
            new("city", "City", !string.IsNullOrWhiteSpace(s.City), "/config"),
            new("businessType", "Business type", !string.IsNullOrWhiteSpace(s.BusinessType), "/config"),
            new("onboardingComplete", "Onboarding finished", s.OnboardingComplete, "/config"),
            new("product", "First product in catalog", hasProduct, "/products"),
            new("paymentMethod", "Payment method added", hasPayment, "/config"),
            new("order", "First order recorded", hasOrder, "/orders"),
        };
        var done = steps.Count(x => x.Done);
        var ageDays = (DateTime.UtcNow - s.CreatedAt).TotalDays;
        string? stuck = null;
        if (done < steps.Count && ageDays > 1) stuck = "Setup is unfinished after 24 hours.";
        else if (hasProduct && !hasOrder && ageDays > 7) stuck = "No order recorded 7 days after setup.";
        return new SetupDto(steps, done, steps.Count, stuck);
    }

    public async Task<DashboardDto> DashboardAsync(int sellerId, AdminDateRange range, CancellationToken ct)
    {
        var length = range.ToDay.DayNumber - range.FromDay.DayNumber + 1;
        var previousFrom = AdminDateRange.ToUtc(range.FromDay.AddDays(-length));
        var previousTo = AdminDateRange.ToUtc(range.FromDay);

        var orders = await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.SellerId == sellerId && o.CreatedAt >= range.StartUtc && o.CreatedAt < range.EndUtc)
            .ToListAsync(ct);
        var sales = orders.Where(o => !NotSales.Contains(o.Status)).ToList();

        var previousRevenue = await db.Orders.AsNoTracking()
            .Where(o => o.SellerId == sellerId && o.CreatedAt >= previousFrom && o.CreatedAt < previousTo && !NotSales.Contains(o.Status))
            .SumAsync(o => (double)o.Total, ct);
        var pending = await db.Orders.CountAsync(o => o.SellerId == sellerId && o.Status == OrderStatus.Pending, ct);

        var revenue = Money(sales.Sum(o => o.Total));
        var kpis = new KpisDto(
            revenue,
            sales.Count,
            pending,
            sales.Select(o => o.CustomerId).Distinct().Count(),
            sales.Count == 0 ? 0 : Money(revenue / sales.Count),
            previousRevenue > 0 ? ((double)revenue - previousRevenue) / previousRevenue * 100 : null);

        var byDay = sales.GroupBy(o => AdminDateRange.DayOf(o.CreatedAt))
            .ToDictionary(g => g.Key, g => (Revenue: g.Sum(o => o.Total), Orders: g.Count()));
        var days = new List<DayPointDto>();
        for (var day = range.FromDay; day <= range.ToDay; day = day.AddDays(1))
        {
            byDay.TryGetValue(day, out var v);
            days.Add(new DayPointDto(day.ToString(AdminDateRange.DateFormat), Money(v.Revenue), v.Orders));
        }

        var statusCounts = Enum.GetNames<OrderStatus>().ToDictionary(n => n, _ => 0);
        foreach (var o in orders) statusCounts[o.Status.ToString()]++;

        var recent = await OrderRows(db.Orders.AsNoTracking().Where(o => o.SellerId == sellerId)
                .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Take(RecentOrderCount))
            .ToListAsync(ct);

        var lowStock = await db.Products.AsNoTracking()
            .Where(p => p.SellerId == sellerId && p.IsActive && p.StockQty != null && p.StockQty <= LowStockThreshold)
            .OrderBy(p => p.StockQty).ThenBy(p => p.Id).Take(LowStockCount).ToListAsync(ct);

        return new DashboardDto(
            new DateRangeDto(range.FromDay.ToString(AdminDateRange.DateFormat), range.ToDay.ToString(AdminDateRange.DateFormat)),
            kpis,
            days,
            statusCounts,
            await CategoryMixAsync(sellerId, sales, ct),
            recent.Select(ToOrderSummary).ToList(),
            lowStock.Select(ToProduct).ToList());
    }

    public async Task<PagedDto<CustomerDto>> CustomersAsync(int sellerId, string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Customers.AsNoTracking().Where(c => c.SellerId == sellerId && c.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(c => EF.Functions.Like(c.Name, $"%{term}%") || (c.Phone != null && EF.Functions.Like(c.Phone, $"%{term}%")) || (c.City != null && EF.Functions.Like(c.City, $"%{term}%")));
        }

        var total = await query.CountAsync(ct);
        var (current, size) = Paging(page, pageSize);
        var rows = await query.OrderBy(c => c.Name).ThenBy(c => c.Id).Skip((current - 1) * size).Take(size).ToListAsync(ct);
        var stats = await CustomerStatsAsync(sellerId, rows.Select(r => r.Id).ToList(), ct);
        return new PagedDto<CustomerDto>(rows.Select(c => ToCustomer(c, stats)).ToList(), current, size, total);
    }

    public async Task<CustomerDto?> CustomerAsync(int sellerId, int id, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.SellerId == sellerId && x.DeletedAt == null, ct);
        if (c is null) return null;
        var stats = await CustomerStatsAsync(sellerId, [c.Id], ct);
        return ToCustomer(c, stats);
    }

    public async Task<PagedDto<ProductDto>> ProductsAsync(int sellerId, string? search, string? category, string status, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Products.AsNoTracking().Where(p => p.SellerId == sellerId);
        query = status switch
        {
            "inactive" => query.Where(p => !p.IsActive),
            "all" => query,
            _ => query.Where(p => p.IsActive),
        };
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(p => p.Category == category);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p => EF.Functions.Like(p.Name, $"%{term}%") || (p.Sku != null && EF.Functions.Like(p.Sku, $"%{term}%")) || (p.Category != null && EF.Functions.Like(p.Category, $"%{term}%")));
        }

        var total = await query.CountAsync(ct);
        var (current, size) = Paging(page, pageSize);
        var rows = await query.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip((current - 1) * size).Take(size).ToListAsync(ct);
        return new PagedDto<ProductDto>(rows.Select(ToProduct).ToList(), current, size, total);
    }

    public async Task<ProductDto?> ProductAsync(int sellerId, int id, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.SellerId == sellerId, ct);
        return p is null ? null : ToProduct(p);
    }

    public async Task<IReadOnlyList<CategoryDto>> CategoriesAsync(int sellerId, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.SellerId == sellerId).ToListAsync(ct);
        var revenue = await SaleLinesAsync(sellerId, ct);
        var revenueByProduct = revenue.Where(l => l.ProductId is not null)
            .GroupBy(l => l.ProductId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Revenue));
        var revenueByName = revenue.GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.Sum(l => l.Revenue));

        return products
            .GroupBy(p => Label(p.Category))
            .Select(g => new CategoryDto(
                g.Key,
                g.Count(),
                Money(g.Sum(p => ProductRevenue(p, revenueByProduct, revenueByName))),
                g.GroupBy(p => Label(Subcategory(p.AttributesJson)))
                    .Select(s => new CategorySubDto(s.Key, s.Count(), Money(s.Sum(p => ProductRevenue(p, revenueByProduct, revenueByName)))))
                    .OrderByDescending(s => s.Revenue).ThenBy(s => s.Name).ToList()))
            .OrderByDescending(c => c.Revenue).ThenBy(c => c.Name)
            .ToList();
    }

    public async Task<PagedDto<OrderSummaryDto>> OrdersAsync(
        int sellerId, OrderStatus? status, int? customerId, AdminDateRange? range, string? search, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking().Where(o => o.SellerId == sellerId);
        if (status is { } s) query = query.Where(o => o.Status == s);
        if (customerId is { } cid) query = query.Where(o => o.CustomerId == cid);
        if (range is { } r) query = query.Where(o => o.CreatedAt >= r.StartUtc && o.CreatedAt < r.EndUtc);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().TrimStart('#');
            var isId = int.TryParse(term, out var id);
            query = query.Where(o => EF.Functions.Like(o.Customer!.Name, $"%{term}%") || (isId && o.Id == id));
        }

        var total = await query.CountAsync(ct);
        var (current, size) = Paging(page, pageSize);
        var rows = await OrderRows(query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
                .Skip((current - 1) * size).Take(size))
            .ToListAsync(ct);
        return new PagedDto<OrderSummaryDto>(rows.Select(ToOrderSummary).ToList(), current, size, total);
    }

    public async Task<OrderDetailDto?> OrderAsync(int sellerId, int id, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking().Include(x => x.Items).Include(x => x.Customer)
            .FirstOrDefaultAsync(x => x.Id == id && x.SellerId == sellerId, ct);
        if (o is null) return null;

        var lines = o.Items.OrderBy(i => i.Id)
            .Select(i => new OrderLineDto(i.Id, i.ProductId, i.ProductNameSnapshot, i.UnitPrice, i.UnitCost, i.Quantity, Money(i.UnitPrice * i.Quantity)))
            .ToList();
        return new OrderDetailDto(
            o.Id, o.CustomerId, o.Customer!.Name, o.Status.ToString(), o.PaymentStatus.ToString(), o.PaymentMethod.ToString(),
            o.Total, o.AmountPaid, o.ReceiptNumber, Utc(o.CreatedAt),
            o.Subtotal, o.DiscountAmount, o.DiscountCode, o.DeliveryCharge,
            o.TrackingCourier, o.TrackingNumber, o.Notes,
            Utc(o.ShippedAt), Utc(o.DeliveredAt), Utc(o.CancelledAt), Utc(o.ReturnedAt), lines);
    }

    // ----- helpers -----

    /// <summary>
    /// The analysis page. Same definitions as the dashboard and the admin reports: sales exclude cancelled and returned
    /// orders, profit is only counted for lines whose cost was recorded, and pending counts every open order.
    /// </summary>
    public async Task<AnalysisDto> AnalysisAsync(int sellerId, AdminDateRange range, CancellationToken ct)
    {
        var length = range.ToDay.DayNumber - range.FromDay.DayNumber + 1;
        var previousFrom = AdminDateRange.ToUtc(range.FromDay.AddDays(-length));
        var previousTo = AdminDateRange.ToUtc(range.FromDay);

        var orders = await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.SellerId == sellerId && o.CreatedAt >= previousFrom && o.CreatedAt < range.EndUtc)
            .ToListAsync(ct);
        var inRange = orders.Where(o => o.CreatedAt >= range.StartUtc).ToList();
        var sales = inRange.Where(o => !NotSales.Contains(o.Status)).ToList();
        var previousRevenue = orders.Where(o => o.CreatedAt < previousTo && !NotSales.Contains(o.Status)).Sum(o => o.Total);
        var pending = await db.Orders.CountAsync(o => o.SellerId == sellerId && o.Status == OrderStatus.Pending, ct);

        var revenue = Money(sales.Sum(o => o.Total));
        var kpis = new KpisDto(
            revenue,
            sales.Count,
            pending,
            sales.Select(o => o.CustomerId).Distinct().Count(),
            sales.Count == 0 ? 0 : Money(revenue / sales.Count),
            previousRevenue > 0 ? ((double)revenue - (double)previousRevenue) / (double)previousRevenue * 100 : null);

        var byDay = sales.GroupBy(o => AdminDateRange.DayOf(o.CreatedAt))
            .ToDictionary(g => g.Key, g => (Revenue: g.Sum(o => o.Total), Orders: g.Count()));
        var daily = new List<DayPointDto>();
        for (var day = range.FromDay; day <= range.ToDay; day = day.AddDays(1))
        {
            byDay.TryGetValue(day, out var v);
            daily.Add(new DayPointDto(day.ToString(AdminDateRange.DateFormat), Money(v.Revenue), v.Orders));
        }

        var lines = sales.SelectMany(o => o.Items).ToList();
        static decimal LineProfit(OrderItem l) => l.UnitCost is { } cost ? l.LineTotal - cost * l.Quantity : 0;
        var topProducts = lines.GroupBy(l => l.ProductNameSnapshot)
            .Select(g => new TopProductDto(g.Key, g.Sum(l => l.Quantity), Money(g.Sum(l => l.LineTotal)), Money(g.Sum(LineProfit))))
            .OrderByDescending(p => p.Revenue).Take(TopProductCount).ToList();

        var paymentMix = sales.GroupBy(o => o.PaymentMethod)
            .Select(g => new PaymentMixDto(g.Key.ToString(), g.Count(), Money(g.Sum(o => o.Total))))
            .OrderByDescending(p => p.Revenue).ToList();

        var statusCounts = Enum.GetNames<OrderStatus>().ToDictionary(n => n, _ => 0);
        foreach (var o in inRange) statusCounts[o.Status.ToString()]++;

        var ordersPerCustomer = sales.GroupBy(o => o.CustomerId).Select(g => g.Count()).ToList();
        var repeatRate = ordersPerCustomer.Count == 0 ? 0 : (double)ordersPerCustomer.Count(n => n > 1) / ordersPerCustomer.Count * 100;

        var newCustomers = await db.Customers.CountAsync(c => c.SellerId == sellerId && c.DeletedAt == null
            && c.CreatedAt >= range.StartUtc && c.CreatedAt < range.EndUtc, ct);

        return new AnalysisDto(
            new DateRangeDto(range.FromDay.ToString(AdminDateRange.DateFormat), range.ToDay.ToString(AdminDateRange.DateFormat)),
            kpis,
            daily,
            topProducts,
            await CategoryMixAsync(sellerId, sales, ct),
            paymentMix,
            statusCounts,
            Math.Round(repeatRate, 2),
            newCustomers,
            Money(lines.Sum(LineProfit)));
    }

    private static readonly Dictionary<string, string> ReportTitles = new()
    {
        ["sales"] = "Sales by day",
        ["products"] = "Product sales",
        ["customers"] = "Customer sales",
        ["payments"] = "Payments",
    };

    private static readonly Dictionary<OrderPaymentMethod, string> PaymentLabels = new()
    {
        [OrderPaymentMethod.Cod] = "Cash on delivery",
        [OrderPaymentMethod.Manual] = "Manual transfer",
        [OrderPaymentMethod.Gateway] = "Online gateway",
        [OrderPaymentMethod.Unspecified] = "Not set",
    };

    /// <summary>
    /// A report as a table, for the Reports page and its CSV download. Same rules as the dashboard's report builder:
    /// cancelled and returned orders are left out, and profit counts only lines whose cost was saved.
    /// Returns null for an unknown report id.
    /// </summary>
    public async Task<ReportTableDto?> ReportAsync(int sellerId, string id, AdminDateRange range, CancellationToken ct)
    {
        if (!ReportTitles.ContainsKey(id)) return null;

        var orders = await db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Customer)
            .Where(o => o.SellerId == sellerId && o.CreatedAt >= range.StartUtc && o.CreatedAt < range.EndUtc && !NotSales.Contains(o.Status))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .ToListAsync(ct);
        var categories = await CategoryLookupsAsync(sellerId, ct);

        var parts = id switch
        {
            "sales" => SalesReport(orders),
            "products" => ProductsReport(orders, categories),
            "customers" => CustomersReport(orders),
            _ => PaymentsReport(orders),
        };

        return new ReportTableDto(
            id, ReportTitles[id], new DateRangeDto(range.FromDay.ToString(AdminDateRange.DateFormat), range.ToDay.ToString(AdminDateRange.DateFormat)),
            parts.Columns, parts.Rows, Totals(parts.Rows, parts.TotalKeys), parts.Note);
    }

    private static ReportParts SalesReport(List<Order> orders)
    {
        var rows = orders.GroupBy(o => AdminDateRange.DayOf(o.CreatedAt)).OrderBy(g => g.Key).Select(g => Row(
            ("date", g.Key.ToString(AdminDateRange.DateFormat)),
            ("orders", g.Count()),
            ("subtotal", Money(g.Sum(o => o.Subtotal))),
            ("discount", Money(g.Sum(o => o.DiscountAmount))),
            ("delivery", Money(g.Sum(o => o.DeliveryCharge))),
            ("total", Money(g.Sum(o => o.Total))))).ToList();
        var columns = new List<ReportColumnDto>
        {
            new("date", "Date", "date"), new("orders", "Orders", "number"), new("subtotal", "Items", "money"),
            new("discount", "Discount", "money"), new("delivery", "Delivery", "money"), new("total", "Total", "money"),
        };
        return new ReportParts(columns, rows, ["orders", "subtotal", "discount", "delivery", "total"], null);
    }

    private static ReportParts ProductsReport(List<Order> orders, (Dictionary<int, string?> ById, Dictionary<string, string?> ByName) categories)
    {
        var groups = new Dictionary<string, (string? Category, int Units, decimal Revenue, decimal? Cost, decimal CostedRevenue)>();
        foreach (var line in orders.SelectMany(o => o.Items))
        {
            var g = groups.GetValueOrDefault(line.ProductNameSnapshot);
            var category = g.Category ?? CategoryOf(line, categories);
            var cost = line.UnitCost is { } unitCost ? (g.Cost ?? 0) + unitCost * line.Quantity : g.Cost;
            var costedRevenue = line.UnitCost is null ? g.CostedRevenue : g.CostedRevenue + line.LineTotal;
            groups[line.ProductNameSnapshot] = (category, g.Units + line.Quantity, g.Revenue + line.LineTotal, cost, costedRevenue);
        }

        var rows = groups
            .OrderByDescending(kv => kv.Value.Revenue)
            .Select(kv =>
            {
                var (category, units, revenue, cost, costedRevenue) = kv.Value;
                var profit = cost is null ? (decimal?)null : Money(costedRevenue - cost.Value);
                return Row(
                    ("product", kv.Key),
                    ("category", category),
                    ("units", units),
                    ("revenue", Money(revenue)),
                    ("cost", cost is null ? null : Money(cost.Value)),
                    ("profit", profit),
                    ("margin", profit is null || costedRevenue == 0 ? null : Money(profit.Value / costedRevenue * 100)));
            }).ToList();

        var columns = new List<ReportColumnDto>
        {
            new("product", "Product", "text"), new("category", "Category", "text"), new("units", "Units", "number"),
            new("revenue", "Revenue", "money"), new("cost", "Cost", "money"), new("profit", "Profit", "money"), new("margin", "Margin", "percent"),
        };
        var someUncosted = rows.Any(r => r["cost"] is null);
        return new ReportParts(columns, rows, ["units", "revenue", "cost", "profit"],
            someUncosted ? "Profit counts only items whose cost was saved when the order was made. A blank cost means none was recorded." : null);
    }

    private static ReportParts CustomersReport(List<Order> orders)
    {
        var rows = orders.GroupBy(o => o.CustomerId)
            .OrderByDescending(g => g.Sum(o => o.Total))
            .Select(g =>
            {
                var customer = g.First().Customer!;
                return Row(
                    ("customer", customer.Name),
                    ("phone", customer.Phone),
                    ("city", customer.City),
                    ("orders", g.Count()),
                    ("spent", Money(g.Sum(o => o.Total))),
                    ("lastOrder", AdminDateRange.DayOf(g.Max(o => o.CreatedAt)).ToString(AdminDateRange.DateFormat)));
            }).ToList();
        var columns = new List<ReportColumnDto>
        {
            new("customer", "Customer", "text"), new("phone", "Phone", "text"), new("city", "City", "text"),
            new("orders", "Orders", "number"), new("spent", "Spent", "money"), new("lastOrder", "Last order", "date"),
        };
        return new ReportParts(columns, rows, ["orders", "spent"], null);
    }

    private static ReportParts PaymentsReport(List<Order> orders)
    {
        var rows = orders.GroupBy(o => o.PaymentMethod)
            .OrderByDescending(g => g.Sum(o => o.Total))
            .Select(g =>
            {
                var billed = g.Sum(o => o.Total);
                // Same rule as the rest of the bot: a Paid order is fully received, even if its amount was never filled in.
                var received = g.Sum(o => o.PaymentStatus == PaymentStatus.Paid ? o.Total : Math.Min(o.AmountPaid, o.Total));
                return Row(
                    ("method", PaymentLabels.GetValueOrDefault(g.Key, g.Key.ToString())),
                    ("orders", g.Count()),
                    ("billed", Money(billed)),
                    ("received", Money(received)),
                    ("balance", Money(billed - received)));
            }).ToList();
        var columns = new List<ReportColumnDto>
        {
            new("method", "Payment method", "text"), new("orders", "Orders", "number"), new("billed", "Billed", "money"),
            new("received", "Received", "money"), new("balance", "Still due", "money"),
        };
        return new ReportParts(columns, rows, ["orders", "billed", "received", "balance"], null);
    }

    private sealed record ReportParts(
        List<ReportColumnDto> Columns, List<IReadOnlyDictionary<string, object?>> Rows, string[] TotalKeys, string? Note);

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value);

    /// <summary>Sums of the given columns. A column with no numbers at all is null, not 0.</summary>
    private static IReadOnlyDictionary<string, decimal?> Totals(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string[] keys) =>
        keys.ToDictionary(k => k, k =>
        {
            var values = rows.Select(r => r[k]).Where(v => v is int or decimal).Select(Convert.ToDecimal).ToList();
            return values.Count == 0 ? (decimal?)null : Money(values.Sum());
        });

    private async Task<(Dictionary<int, string?> ById, Dictionary<string, string?> ByName)> CategoryLookupsAsync(int sellerId, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.SellerId == sellerId)
            .Select(p => new { p.Id, p.Name, p.Category }).ToListAsync(ct);
        return (products.ToDictionary(p => p.Id, p => p.Category), products.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().Category));
    }

    private static string? CategoryOf(OrderItem line, (Dictionary<int, string?> ById, Dictionary<string, string?> ByName) categories)
    {
        string? category = null;
        if (line.ProductId is int productId) categories.ById.TryGetValue(productId, out category);
        if (string.IsNullOrWhiteSpace(category)) categories.ByName.TryGetValue(line.ProductNameSnapshot, out category);
        return string.IsNullOrWhiteSpace(category) ? null : category.Trim();
    }

    private async Task<List<CategoryShareDto>> CategoryMixAsync(int sellerId, List<Order> sales, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.SellerId == sellerId)
            .Select(p => new { p.Id, p.Name, p.Category }).ToListAsync(ct);
        var categoryById = products.ToDictionary(p => p.Id, p => p.Category);
        var categoryByName = products.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().Category);

        var totals = new Dictionary<string, (decimal Revenue, int Units)>();
        foreach (var line in sales.SelectMany(o => o.Items))
        {
            string? category = null;
            if (line.ProductId is int pid) categoryById.TryGetValue(pid, out category);
            if (string.IsNullOrWhiteSpace(category)) categoryByName.TryGetValue(line.ProductNameSnapshot, out category);

            var key = Label(category);
            var current = totals.GetValueOrDefault(key);
            totals[key] = (current.Revenue + line.UnitPrice * line.Quantity, current.Units + line.Quantity);
        }

        var grand = totals.Values.Sum(t => t.Revenue);
        return totals
            .Select(kv => new CategoryShareDto(kv.Key, Money(kv.Value.Revenue), grand > 0 ? (double)(kv.Value.Revenue / grand) * 100 : 0, kv.Value.Units))
            .OrderByDescending(c => c.Revenue).ToList();
    }

    private async Task<List<SaleLine>> SaleLinesAsync(int sellerId, CancellationToken ct)
    {
        var rows = await db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.SellerId == sellerId && !NotSales.Contains(i.Order.Status))
            .Select(i => new { i.ProductId, i.ProductNameSnapshot, i.UnitPrice, i.Quantity })
            .ToListAsync(ct);
        return rows.Select(r => new SaleLine(r.ProductId, r.ProductNameSnapshot, r.UnitPrice * r.Quantity)).ToList();
    }

    private static decimal ProductRevenue(Product p, IReadOnlyDictionary<int, decimal> byId, IReadOnlyDictionary<string, decimal> byName) =>
        byId.TryGetValue(p.Id, out var fromId) ? fromId : byName.GetValueOrDefault(p.Name);

    private async Task<Dictionary<int, CustomerStat>> CustomerStatsAsync(int sellerId, List<int> customerIds, CancellationToken ct)
    {
        if (customerIds.Count == 0) return new();
        var rows = await db.Orders.AsNoTracking()
            .Where(o => o.SellerId == sellerId && customerIds.Contains(o.CustomerId))
            .GroupBy(o => o.CustomerId)
            .Select(g => new
            {
                CustomerId = g.Key,
                OrderCount = g.Count(),
                Spent = g.Sum(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned ? (double)o.Total : 0.0),
                LastAt = g.Max(o => o.CreatedAt),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.CustomerId, r => new CustomerStat(r.OrderCount, Money((decimal)r.Spent), r.LastAt));
    }

    private static IQueryable<OrderRow> OrderRows(IQueryable<Order> orders) => orders.Select(o => new OrderRow(
        o.Id, o.CustomerId, o.Customer!.Name, o.Status, o.PaymentStatus, o.PaymentMethod,
        o.Total, o.AmountPaid, o.ReceiptNumber, o.CreatedAt));

    private static OrderSummaryDto ToOrderSummary(OrderRow r) => new(
        r.Id, r.CustomerId, r.CustomerName, r.Status.ToString(), r.PaymentStatus.ToString(), r.PaymentMethod.ToString(),
        r.Total, r.AmountPaid, r.ReceiptNumber, Utc(r.CreatedAt));

    private static CustomerDto ToCustomer(Customer c, IReadOnlyDictionary<int, CustomerStat> stats)
    {
        stats.TryGetValue(c.Id, out var stat);
        return new CustomerDto(
            c.Id, c.Name, c.Phone, c.City, c.Address, c.PreferredContact, c.Notes,
            stat?.OrderCount ?? 0, stat?.TotalSpent ?? 0, Utc(stat?.LastOrderAt), Utc(c.CreatedAt));
    }

    private static ProductDto ToProduct(Product p) => new(
        p.Id, p.Name, p.Price, p.CostPrice, p.UnitType, p.UnitQty, p.Category, Subcategory(p.AttributesJson),
        p.Size, p.Color, p.Sku, p.StockQty, p.IsActive, Utc(p.CreatedAt));

    /// <summary>Subcategory lives in AttributesJson until the bot gets a column for it (see docs/API_CONTRACT.md).</summary>
    private static string? Subcategory(string? attributesJson)
    {
        if (string.IsNullOrWhiteSpace(attributesJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(attributesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "subcategory", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Label(string? value) => string.IsNullOrWhiteSpace(value) ? "Uncategorized" : value.Trim();

    private static (int Page, int Size) Paging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The seller's WhatsApp chat with the bot, oldest first. With afterId above zero, returns only newer lines
    /// (for polling); otherwise the latest <paramref name="limit"/> lines.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessageDto>> MessagesAsync(int sellerId, int afterId, int limit, CancellationToken ct)
    {
        var phone = await db.Sellers.AsNoTracking().Where(s => s.Id == sellerId).Select(s => s.WhatsAppPhoneNumber).FirstOrDefaultAsync(ct);
        if (phone is null) return [];

        limit = Math.Clamp(limit, 1, MaxMessages);
        var chat = db.MessageLogs.AsNoTracking().Where(m => m.Phone == phone);
        if (afterId > 0)
        {
            var newer = await chat.Where(m => m.Id > afterId).OrderBy(m => m.Id).Take(limit).ToListAsync(ct);
            return newer.Select(ToChatMessage).ToList();
        }

        var latest = await chat.OrderByDescending(m => m.Id).Take(limit).ToListAsync(ct);
        latest.Reverse();
        return latest.Select(ToChatMessage).ToList();
    }

    private static ChatMessageDto ToChatMessage(MessageLog m) => new(m.Id, Utc(m.CreatedAt), m.Direction, m.RawText);

    /// <summary>
    /// Stored timestamps are UTC, but EF returns them as Unspecified. Without the marker, JSON has no "Z" and
    /// browsers read the value as local time.
    /// </summary>
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? value) => value is null ? null : Utc(value.Value);

    private sealed record OrderRow(
        int Id, int CustomerId, string CustomerName, OrderStatus Status, PaymentStatus PaymentStatus,
        OrderPaymentMethod PaymentMethod, decimal Total, decimal AmountPaid, int? ReceiptNumber, DateTime CreatedAt);

    private sealed record CustomerStat(int OrderCount, decimal TotalSpent, DateTime? LastOrderAt);

    private sealed record SaleLine(int? ProductId, string Name, decimal Revenue);
}
