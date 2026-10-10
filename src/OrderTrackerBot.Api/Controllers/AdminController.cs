using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Controllers;

/// <summary>
/// Admin panel API: overview stats, the seller (user) list and detail, the order list, and setting a seller's plan.
/// Every action requires the X-Admin-Api-Key header (see <see cref="AdminApiKeyFilter"/>).
/// </summary>
[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminApiKeyFilter))]
public class AdminController : ControllerBase
{
    private const int MaxPageSize = 100;

    private readonly AppDbContext _db;
    private readonly ILogger<AdminController> _logger;

    public AdminController(AppDbContext db, ILogger<AdminController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Dashboard counters. Revenue excludes cancelled and returned orders; "last 24 hours" is UTC-based.</summary>
    [HttpGet("stats")]
    public async Task<ActionResult<AdminStatsDto>> GetStats(CancellationToken ct)
    {
        var sellers = _db.Sellers.AsNoTracking();
        var orders = _db.Orders.AsNoTracking();
        var since = DateTime.UtcNow.AddHours(-24);
        var sales = orders.Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned);

        var totalSellers = await sellers.CountAsync(ct);
        var onboarded = await sellers.CountAsync(s => s.OnboardingComplete, ct);
        var totalOrders = await orders.CountAsync(ct);
        var pendingOrders = await orders.CountAsync(o => o.Status == OrderStatus.Pending, ct);
        var ordersLast24Hours = await orders.CountAsync(o => o.CreatedAt >= since, ct);
        // Cast to double so SQLite (which can't SUM decimal) can aggregate; converted back below.
        var revenue = await sales.SumAsync(o => (double)o.Total, ct);

        var planRows = await sellers.GroupBy(s => s.Plan)
            .Select(g => new { Plan = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var sellersByPlan = Enum.GetNames<SubscriptionPlan>().ToDictionary(name => name, _ => 0);
        foreach (var row in planRows)
            sellersByPlan[row.Plan.ToString()] = row.Count;

        return Ok(new AdminStatsDto(
            totalSellers,
            onboarded,
            totalOrders,
            pendingOrders,
            ordersLast24Hours,
            Math.Round((decimal)revenue, 2),
            sellersByPlan));
    }

    /// <summary>Paged seller list, newest first. <paramref name="search"/> matches phone, business name or city.</summary>
    [HttpGet("sellers")]
    public async Task<ActionResult<PagedResultDto<SellerListItemDto>>> ListSellers(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        IQueryable<Seller> query = _db.Sellers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(s => s.WhatsAppPhoneNumber.Contains(term)
                || (s.BusinessName != null && s.BusinessName.Contains(term))
                || (s.City != null && s.City.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new
            {
                s.Id, s.WhatsAppPhoneNumber, s.BusinessName, s.City, s.BusinessType, s.OnboardingComplete,
                s.Plan, s.TrialEndsAt, s.SubscriptionActiveUntil, s.CreatedAt,
                OrderCount = s.Orders.Count(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new SellerListItemDto(
            r.Id, r.WhatsAppPhoneNumber, r.BusinessName, r.City, r.BusinessType, r.OnboardingComplete,
            r.Plan.ToString(), r.TrialEndsAt, r.SubscriptionActiveUntil, r.CreatedAt, r.OrderCount)).ToList();

        return Ok(new PagedResultDto<SellerListItemDto>(items, page, pageSize, total));
    }

    /// <summary>One seller with counts and their 10 most recent orders.</summary>
    [HttpGet("sellers/{id:int}")]
    public async Task<ActionResult<SellerDetailDto>> GetSeller(int id, CancellationToken ct)
    {
        var s = await _db.Sellers.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.WhatsAppPhoneNumber, x.BusinessName, x.City, x.BusinessType, x.OnboardingComplete,
                x.PreferredLanguage, x.TimeZoneId, x.InstagramHandle, x.Ntn, x.Strn, x.DefaultDeliveryCharge,
                x.SalesTaxRate, x.Plan, x.TrialEndsAt, x.SubscriptionActiveUntil, x.CreatedAt,
                OrderCount = x.Orders.Count(),
                ProductCount = x.Products.Count(),
                CustomerCount = x.Customers.Count(),
            })
            .FirstOrDefaultAsync(ct);

        if (s is null) return NotFound(new { error = "Seller not found." });

        var recentOrders = await LoadOrdersAsync(_db.Orders.AsNoTracking().Where(o => o.SellerId == id), 0, 10, ct);

        return Ok(new SellerDetailDto(
            s.Id, s.WhatsAppPhoneNumber, s.BusinessName, s.City, s.BusinessType, s.OnboardingComplete,
            s.PreferredLanguage, s.TimeZoneId, s.InstagramHandle, s.Ntn, s.Strn, s.DefaultDeliveryCharge,
            s.SalesTaxRate, s.Plan.ToString(), s.TrialEndsAt, s.SubscriptionActiveUntil, s.CreatedAt,
            s.OrderCount, s.ProductCount, s.CustomerCount, recentOrders));
    }

    /// <summary>
    /// Sets a seller's plan and paid-until date (the manual step after a payment is verified; billing trusts this field).
    /// Trial clears the paid-until date; Basic and Pro require one.
    /// </summary>
    [HttpPut("sellers/{id:int}/subscription")]
    public async Task<ActionResult<SellerDetailDto>> UpdateSubscription(int id, [FromBody] UpdateSubscriptionRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<SubscriptionPlan>(request.Plan, ignoreCase: true, out var plan) || !Enum.IsDefined(plan))
            return BadRequest(new { error = "Plan must be one of: Trial, Basic, Pro." });

        var seller = await _db.Sellers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (seller is null) return NotFound(new { error = "Seller not found." });

        if (plan != SubscriptionPlan.Trial && request.SubscriptionActiveUntil is null)
            return BadRequest(new { error = "Basic and Pro need subscriptionActiveUntil." });

        seller.Plan = plan;
        seller.SubscriptionActiveUntil = plan == SubscriptionPlan.Trial ? null : ToUtc(request.SubscriptionActiveUntil!.Value);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin set seller {SellerId} to plan {Plan}, paid until {Until}", id, plan, seller.SubscriptionActiveUntil);
        return await GetSeller(id, ct);
    }

    /// <summary>Paged order list, newest first. Optional filters: sellerId and status (Pending, Shipped, Delivered, Cancelled, Returned).</summary>
    [HttpGet("orders")]
    public async Task<ActionResult<PagedResultDto<OrderListItemDto>>> ListOrders(
        [FromQuery] int? sellerId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        IQueryable<Order> query = _db.Orders.AsNoTracking();
        if (sellerId is { } sid) query = query.Where(o => o.SellerId == sid);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return BadRequest(new { error = $"Unknown order status '{status}'." });
            query = query.Where(o => o.Status == parsed);
        }

        var total = await query.CountAsync(ct);
        var items = await LoadOrdersAsync(query, (page - 1) * pageSize, pageSize, ct);
        return Ok(new PagedResultDto<OrderListItemDto>(items, page, pageSize, total));
    }

    private static async Task<List<OrderListItemDto>> LoadOrdersAsync(IQueryable<Order> query, int skip, int take, CancellationToken ct)
    {
        var rows = await query
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip(skip).Take(take)
            .Select(o => new
            {
                o.Id, o.SellerId, SellerBusinessName = o.Seller!.BusinessName, SellerPhone = o.Seller!.WhatsAppPhoneNumber,
                CustomerName = o.Customer!.Name, o.Status, o.PaymentStatus, o.Total, o.AmountPaid, o.ReceiptNumber, o.CreatedAt,
            })
            .ToListAsync(ct);

        return rows.Select(r => new OrderListItemDto(
            r.Id, r.SellerId, r.SellerBusinessName, r.SellerPhone, r.CustomerName, r.Status.ToString(),
            r.PaymentStatus.ToString(), r.Total, r.AmountPaid, r.ReceiptNumber, r.CreatedAt)).ToList();
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc), // unspecified (e.g. "2026-11-10"): treat as UTC
    };
}
