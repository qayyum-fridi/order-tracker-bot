using Microsoft.AspNetCore.Mvc;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Api.Controllers;

/// <summary>
/// Seller dashboard API, read-only in this version. Every route is scoped to the seller who owns the bearer token
/// (see <see cref="ClientAuthFilter"/>). Date parameters use the same yyyy-MM-dd Pakistan-day rules as the admin API.
/// </summary>
[ApiController]
[Route("api/client")]
[ServiceFilter(typeof(ClientAuthFilter))]
public class ClientController(ClientQueries queries) : ControllerBase
{
    private const int DefaultPageSize = 25;

    private int SellerId => (int)HttpContext.Items[ClientAuthFilter.SellerIdItem]!;

    [HttpGet("me")]
    public async Task<ActionResult<ClientProfileDto>> Me(CancellationToken ct) =>
        Found(await queries.ProfileAsync(SellerId, ct), "Seller not found.");

    [HttpGet("setup")]
    public async Task<ActionResult<SetupDto>> Setup(CancellationToken ct) =>
        Found(await queries.SetupAsync(SellerId, ct), "Seller not found.");

    /// <summary>Dashboard numbers for a range (default: last 14 days). Revenue excludes cancelled and returned orders.</summary>
    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardDto>> Dashboard([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        if (!AdminDateRange.TryResolve(from, to, 14, out var range, out var error)) return BadRequest(new { error });
        return Ok(await queries.DashboardAsync(SellerId, range, ct));
    }

    [HttpGet("customers")]
    public async Task<ActionResult<PagedDto<CustomerDto>>> Customers(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize, CancellationToken ct = default) =>
        Ok(await queries.CustomersAsync(SellerId, search, page, pageSize, ct));

    [HttpGet("customers/{id:int}")]
    public async Task<ActionResult<CustomerDto>> Customer(int id, CancellationToken ct) =>
        Found(await queries.CustomerAsync(SellerId, id, ct), "Client not found.");

    [HttpGet("products")]
    public async Task<ActionResult<PagedDto<ProductDto>>> Products(
        [FromQuery] string? search, [FromQuery] string? category, [FromQuery] string? status = "active",
        [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize, CancellationToken ct = default)
    {
        if (status is not ("active" or "inactive" or "all")) return BadRequest(new { error = "Status must be active, inactive or all." });
        return Ok(await queries.ProductsAsync(SellerId, search, category, status, page, pageSize, ct));
    }

    [HttpGet("products/{id:int}")]
    public async Task<ActionResult<ProductDto>> Product(int id, CancellationToken ct) =>
        Found(await queries.ProductAsync(SellerId, id, ct), "Product not found.");

    [HttpGet("categories")]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> Categories(CancellationToken ct) =>
        Ok(await queries.CategoriesAsync(SellerId, ct));

    [HttpGet("orders")]
    public async Task<ActionResult<PagedDto<OrderSummaryDto>>> Orders(
        [FromQuery] string? status, [FromQuery] int? customerId, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize, CancellationToken ct = default)
    {
        OrderStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status) && status != "all")
        {
            if (!Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return BadRequest(new { error = $"Unknown order status '{status}'." });
            statusFilter = parsed;
        }
        if (!AdminDateRange.TryResolveOptional(from, to, out var range, out var error)) return BadRequest(new { error });

        return Ok(await queries.OrdersAsync(SellerId, statusFilter, customerId, range, search, page, pageSize, ct));
    }

    [HttpGet("orders/{id:int}")]
    public async Task<ActionResult<OrderDetailDto>> Order(int id, CancellationToken ct) =>
        Found(await queries.OrderAsync(SellerId, id, ct), "Order not found.");

    private static ActionResult<T> Found<T>(T? value, string notFound) where T : class =>
        value is null ? new NotFoundObjectResult(new { error = notFound }) : new OkObjectResult(value);
}
