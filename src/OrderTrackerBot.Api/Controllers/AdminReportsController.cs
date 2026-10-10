using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Controllers;

/// <summary>
/// Admin panel reports: daily numbers, seller setup progress and stuck sellers, and the separate error and issue lists.
/// Every action requires the X-Admin-Api-Key header (see <see cref="AdminApiKeyFilter"/>).
/// </summary>
[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminApiKeyFilter))]
public class AdminReportsController : ControllerBase
{
    private const string DateFormat = "yyyy-MM-dd";
    private const int DefaultRangeDays = 30;
    private const int MaxRangeDays = 366;
    private const int MaxPageSize = 100;
    private const int SetupStepCount = 7;
    private static readonly TimeSpan StalledAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan NoOrderAfter = TimeSpan.FromDays(7);
    private static readonly TimeZoneInfo SellerZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    private readonly AppDbContext _db;

    public AdminReportsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Per-day sellers, active sellers, messages, orders, revenue, errors and issues for a date range (default: last 30 days).</summary>
    [HttpGet("reports/daily")]
    public async Task<ActionResult<DailyReportDto>> Daily([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        if (!AdminDateRange.TryResolve(from, to, DefaultRangeDays, out var range, out var error)) return BadRequest(new { error });

        var inbound = await _db.MessageLogs.AsNoTracking()
            .Where(m => m.Direction == "inbound" && m.CreatedAt >= range.StartUtc && m.CreatedAt < range.EndUtc)
            .Select(m => new { m.Phone, m.CreatedAt })
            .ToListAsync(ct);
        var sellers = await _db.Sellers.AsNoTracking()
            .Where(s => s.CreatedAt >= range.StartUtc && s.CreatedAt < range.EndUtc)
            .Select(s => s.CreatedAt)
            .ToListAsync(ct);
        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.CreatedAt >= range.StartUtc && o.CreatedAt < range.EndUtc)
            .Select(o => new OrderRow(o.CreatedAt, o.Status, o.Total))
            .ToListAsync(ct);
        var issues = await _db.IssueRecords.AsNoTracking()
            .Where(i => i.CreatedAt >= range.StartUtc && i.CreatedAt < range.EndUtc)
            .Select(i => new { i.CreatedAt, i.Severity })
            .ToListAsync(ct);

        var newSellers = sellers.GroupBy(DayOf).ToDictionary(g => g.Key, g => g.Count());
        var messages = inbound.GroupBy(m => DayOf(m.CreatedAt)).ToDictionary(g => g.Key, g => g.Count());
        var active = inbound.GroupBy(m => DayOf(m.CreatedAt)).ToDictionary(g => g.Key, g => g.Select(m => m.Phone).Distinct().Count());
        var orderCount = orders.GroupBy(o => DayOf(o.CreatedAt)).ToDictionary(g => g.Key, g => g.Count());
        var revenue = orders.Where(IsSale).GroupBy(o => DayOf(o.CreatedAt)).ToDictionary(g => g.Key, g => g.Sum(o => o.Total));
        var errors = issues.Where(i => i.Severity == "Error").GroupBy(i => DayOf(i.CreatedAt)).ToDictionary(g => g.Key, g => g.Count());
        var warnings = issues.Where(i => i.Severity == "Warning").GroupBy(i => DayOf(i.CreatedAt)).ToDictionary(g => g.Key, g => g.Count());

        var days = new List<DailyRowDto>();
        for (var day = range.FromDay; day <= range.ToDay; day = day.AddDays(1))
        {
            days.Add(new DailyRowDto(
                day.ToString(DateFormat, CultureInfo.InvariantCulture),
                newSellers.GetValueOrDefault(day),
                active.GetValueOrDefault(day),
                messages.GetValueOrDefault(day),
                orderCount.GetValueOrDefault(day),
                revenue.GetValueOrDefault(day),
                errors.GetValueOrDefault(day),
                warnings.GetValueOrDefault(day)));
        }

        var totals = new DailyTotalsDto(
            sellers.Count,
            inbound.Select(m => m.Phone).Distinct().Count(),
            inbound.Count,
            orders.Count,
            orders.Where(IsSale).Sum(o => o.Total),
            issues.Count(i => i.Severity == "Error"),
            issues.Count(i => i.Severity == "Warning"));

        return Ok(new DailyReportDto(
            range.FromDay.ToString(DateFormat, CultureInfo.InvariantCulture),
            range.ToDay.ToString(DateFormat, CultureInfo.InvariantCulture),
            totals,
            days));
    }

    /// <summary>
    /// Setup checklist per seller, newest first. status: all (default), incomplete (onboarding not finished),
    /// or stuck (onboarding unfinished after 24 hours, or no order 7 days after setup).
    /// </summary>
    [HttpGet("reports/setup")]
    public async Task<ActionResult<PagedResultDto<SellerSetupDto>>> Setup(
        [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var now = DateTime.UtcNow;
        var stalledCutoff = now - StalledAfter;
        var noOrderCutoff = now - NoOrderAfter;

        IQueryable<Seller> query = _db.Sellers.AsNoTracking();
        switch ((status ?? "all").ToLowerInvariant())
        {
            case "all":
                break;
            case "incomplete":
                query = query.Where(s => !s.OnboardingComplete);
                break;
            case "stuck":
                query = query.Where(s => (!s.OnboardingComplete && s.CreatedAt < stalledCutoff)
                    || (s.OnboardingComplete && !s.Orders.Any() && s.CreatedAt < noOrderCutoff));
                break;
            default:
                return BadRequest(new { error = "status must be all, incomplete or stuck." });
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new
            {
                s.Id, s.WhatsAppPhoneNumber, s.BusinessName, s.City, s.BusinessType, s.OnboardingComplete, s.CreatedAt,
                HasProduct = s.Products.Any(),
                HasPaymentMethod = s.PaymentMethods.Any(),
                HasOrder = s.Orders.Any(),
                SessionState = s.Session != null ? (ConversationState?)s.Session.State : null,
                SessionUpdatedAt = s.Session != null ? (DateTime?)s.Session.UpdatedAt : null,
                LastMessageAt = _db.MessageLogs
                    .Where(m => m.Phone == s.WhatsAppPhoneNumber && m.Direction == "inbound")
                    .Max(m => (DateTime?)m.CreatedAt),
            })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var steps = new SetupStepsDto(
                !string.IsNullOrWhiteSpace(r.BusinessName),
                !string.IsNullOrWhiteSpace(r.City),
                !string.IsNullOrWhiteSpace(r.BusinessType),
                r.OnboardingComplete,
                r.HasProduct,
                r.HasPaymentMethod,
                r.HasOrder);
            var done = new[] { steps.BusinessName, steps.City, steps.BusinessType, steps.OnboardingComplete, steps.Product, steps.PaymentMethod, steps.Order }.Count(x => x);
            var currentStep = !r.OnboardingComplete && r.SessionState is { } state && state != ConversationState.Idle ? StepLabel(state) : null;
            return new SellerSetupDto(
                r.Id, r.WhatsAppPhoneNumber, r.BusinessName, done, SetupStepCount, steps, currentStep,
                StuckReason(r.OnboardingComplete, r.CreatedAt, r.HasOrder, currentStep, r.SessionUpdatedAt, now),
                r.LastMessageAt, r.CreatedAt);
        }).ToList();

        return Ok(new PagedResultDto<SellerSetupDto>(items, page, pageSize, total));
    }

    /// <summary>Counts per problem code for a date range (default: last 30 days), with errors and issues kept apart.</summary>
    [HttpGet("issues/summary")]
    public async Task<ActionResult<IssueSummaryDto>> IssueSummary([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        if (!AdminDateRange.TryResolve(from, to, DefaultRangeDays, out var range, out var error)) return BadRequest(new { error });

        var counts = await _db.IssueRecords.AsNoTracking()
            .Where(i => i.CreatedAt >= range.StartUtc && i.CreatedAt < range.EndUtc)
            .GroupBy(i => new { i.Code, i.Title, i.Severity })
            .Select(g => new { g.Key.Code, g.Key.Title, g.Key.Severity, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        var fromText = range.FromDay.ToString(DateFormat, CultureInfo.InvariantCulture);
        var toText = range.ToDay.ToString(DateFormat, CultureInfo.InvariantCulture);
        return Ok(new IssueSummaryDto(
            fromText,
            toText,
            counts.Where(c => c.Severity == "Error").Select(c => new IssueCountDto(c.Code, c.Title, c.Severity, c.Count)).ToList(),
            counts.Where(c => c.Severity == "Warning").Select(c => new IssueCountDto(c.Code, c.Title, c.Severity, c.Count)).ToList()));
    }

    /// <summary>
    /// The error list (kind=errors: system failures) or the issue list (kind=issues: problems sellers ran into),
    /// newest first. Optional sellerPhone filter. Date range defaults to the last 30 days.
    /// </summary>
    [HttpGet("issues")]
    public async Task<ActionResult<PagedResultDto<IssueRecordDto>>> Issues(
        [FromQuery] string? kind = "errors", [FromQuery] string? from = null, [FromQuery] string? to = null,
        [FromQuery] string? sellerPhone = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var severity = (kind ?? "errors").ToLowerInvariant() switch
        {
            "errors" => "Error",
            "issues" => "Warning",
            _ => null,
        };
        if (severity is null) return BadRequest(new { error = "kind must be errors or issues." });
        if (!AdminDateRange.TryResolve(from, to, DefaultRangeDays, out var range, out var error)) return BadRequest(new { error });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        IQueryable<IssueRecord> query = _db.IssueRecords.AsNoTracking()
            .Where(i => i.Severity == severity && i.CreatedAt >= range.StartUtc && i.CreatedAt < range.EndUtc);
        if (!string.IsNullOrWhiteSpace(sellerPhone)) query = query.Where(i => i.SellerPhone == sellerPhone.Trim());

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new IssueRecordDto(i.Id, i.CreatedAt, i.Code, i.Title, i.Severity, i.SellerPhone, i.BusinessName, i.Detail, i.Error))
            .ToListAsync(ct);

        return Ok(new PagedResultDto<IssueRecordDto>(items, page, pageSize, total));
    }

    /// <summary>Cancelled and returned orders are not sales, so they are left out of revenue.</summary>
    private static bool IsSale(OrderRow order) => order.Status != OrderStatus.Cancelled && order.Status != OrderStatus.Returned;

    private static DateOnly DayOf(DateTime utc) => AdminDateRange.DayOf(utc);

    /// <summary>The first reason a seller needs attention, or null. Same rules as the "stuck" filter in Setup.</summary>
    private static string? StuckReason(bool onboarded, DateTime createdAt, bool hasOrder, string? currentStep, DateTime? stepUpdatedAt, DateTime now)
    {
        if (!onboarded && createdAt < now - StalledAfter)
        {
            var since = stepUpdatedAt ?? createdAt;
            var days = Math.Max(1, (int)(now - since).TotalDays);
            var where = currentStep is null ? "Onboarding not finished" : $"Stopped at {currentStep}";
            return $"{where} for {days} day(s)";
        }
        if (onboarded && !hasOrder && createdAt < now - NoOrderAfter)
            return $"No order {(int)(now - createdAt).TotalDays} days after setup";
        return null;
    }

    /// <summary>"OnboardingBusinessName" -> "onboarding business name", for display in the panel.</summary>
    private static string StepLabel(ConversationState state) =>
        Regex.Replace(state.ToString(), "(?<!^)([A-Z])", " $1").ToLowerInvariant();

    private readonly record struct DateRange(DateOnly FromDay, DateOnly ToDay, DateTime StartUtc, DateTime EndUtc);

    private sealed record OrderRow(DateTime CreatedAt, OrderStatus Status, decimal Total);
}
