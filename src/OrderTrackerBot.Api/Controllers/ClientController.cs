using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Api.Client;
using OrderTrackerBot.Application.Conversation;
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

    /// <summary>Plans an uploaded customer or catalog file. Nothing is saved yet: the file is kept for 15 minutes for the commit.</summary>
    [HttpPost("imports/preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ClientImportLimits.MaxUploadBytes)]
    public async Task<ActionResult<ClientImportPreviewDto>> PreviewImport(
        [FromForm] string? kind, IFormFile? file, [FromServices] ConversationEngine engine, [FromServices] ClientImportStore store, CancellationToken ct)
    {
        if (kind is not ("customers" or "catalog")) return BadRequest(new { error = "Kind must be customers or catalog." });
        if (file is null || file.Length == 0) return BadRequest(new { error = "Choose a file to upload." });
        if (file.Length > ClientImportLimits.MaxFileBytes) return BadRequest(new { error = "The file is larger than 5 MB." });
        if (!ImportFileTypes.IsSupported(file.FileName, file.ContentType))
            return BadRequest(new { error = "This file type is not supported. Use .xlsx, .xls, .ods, .csv or .txt." });

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        var preview = await engine.PreviewClientImportAsync(SellerId, kind, bytes, file.FileName, ct);
        if (preview.Problem is not null) return BadRequest(new { error = ImportProblemMessage(preview.Problem) });

        var id = store.Add(SellerId, kind, file.FileName, bytes, DateTime.UtcNow);
        if (id is null) return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many imports are waiting. Try again in a few minutes." });
        return Ok(ClientImportPreviewDto.From(id, kind, file.FileName, preview));
    }

    /// <summary>Saves the new rows of a previewed file. Existing rows are skipped, and the import can be undone from WhatsApp with "undo".</summary>
    [HttpPost("imports/{id}/commit")]
    public async Task<ActionResult<ClientImportResultDto>> CommitImport(
        string id, [FromServices] ConversationEngine engine, [FromServices] ClientImportStore store, CancellationToken ct)
    {
        var waiting = store.Take(id, SellerId, DateTime.UtcNow);
        if (waiting is null) return NotFound(new { error = "This import expired. Upload the file again." });

        var result = await engine.CommitClientImportAsync(SellerId, waiting.Kind, waiting.Bytes, waiting.FileName, ct);
        if (result is null) return BadRequest(new { error = "Nothing new to import in this file." });
        return Ok(new ClientImportResultDto(result.Created, 0, result.Skipped));
    }

    private static string ImportProblemMessage(string problem) => problem switch
    {
        "TooBig" => "The file is larger than 5 MB.",
        "Unreadable" => "This file could not be read. Check that it is not damaged or password-protected.",
        "NoSheet" => "No customer or product table found. Check the column headings.",
        "TooManyRows" => "The file has too many rows. Split it and upload the parts.",
        _ => "The file could not be imported.",
    };

    /// <summary>Dashboard numbers for a range (default: last 14 days). Revenue excludes cancelled and returned orders.</summary>
    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardDto>> Dashboard([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        if (!AdminDateRange.TryResolve(from, to, 14, out var range, out var error)) return BadRequest(new { error });
        return Ok(await queries.DashboardAsync(SellerId, range, ct));
    }

    /// <summary>Analysis for a range (default: last 30 days). Same numbers as the dashboard, plus product profit and payment mix.</summary>
    [HttpGet("analysis")]
    public async Task<ActionResult<AnalysisDto>> Analysis([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        if (!AdminDateRange.TryResolve(from, to, 30, out var range, out var error)) return BadRequest(new { error });
        return Ok(await queries.AnalysisAsync(SellerId, range, ct));
    }

    /// <summary>A report as a table, the same numbers as the CSV download. Group (day, week or month) applies to the sales report.</summary>
    [HttpGet("reports/{id}")]
    public async Task<ActionResult<ReportTableDto>> Report(
        string id, [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? group, CancellationToken ct)
    {
        if (!AdminDateRange.TryResolve(from, to, 30, out var range, out var error)) return BadRequest(new { error });
        var grouping = string.IsNullOrEmpty(group) ? "day" : group;
        if (!ClientQueries.ReportGroups.Contains(grouping)) return BadRequest(new { error = "Group must be day, week or month." });
        var report = await queries.ReportAsync(SellerId, id, range, grouping, ct);
        return report is null ? NotFound(new { error = "Unknown report." }) : Ok(report);
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

    /// <summary>Adds a product. A blank SKU gets an automatic one.</summary>
    [HttpPost("products")]
    public async Task<ActionResult<ProductDto>> CreateProduct([FromBody] ClientProductInput input, [FromServices] ClientProductWriter writer, CancellationToken ct) =>
        await SavedProduct(await writer.CreateAsync(SellerId, input, ct), ct);

    /// <summary>Replaces a product's details. A field left empty is cleared.</summary>
    [HttpPut("products/{id:int}")]
    public async Task<ActionResult<ProductDto>> UpdateProduct(int id, [FromBody] ClientProductInput input, [FromServices] ClientProductWriter writer, CancellationToken ct) =>
        await SavedProduct(await writer.UpdateAsync(SellerId, id, input, ct), ct);

    /// <summary>Hides a product from the catalog. It is not deleted, so past orders still show what was sold.</summary>
    [HttpDelete("products/{id:int}")]
    public async Task<ActionResult> DeactivateProduct(int id, [FromServices] ClientProductWriter writer, CancellationToken ct)
    {
        var result = await writer.DeactivateAsync(SellerId, id, ct);
        return result.Error is null ? NoContent() : StatusCode(result.Status, new { error = result.Error });
    }

    private async Task<ActionResult<ProductDto>> SavedProduct(ProductWriteResult result, CancellationToken ct)
    {
        if (result.Error is not null || result.Id is null) return StatusCode(result.Status, new { error = result.Error });
        return Ok(await queries.ProductAsync(SellerId, result.Id.Value, ct));
    }

    /// <summary>The seller's WhatsApp chat with the bot. Poll with the last id you have as afterId to get only new lines.</summary>
    [HttpGet("messages")]
    public async Task<ActionResult<IReadOnlyList<ChatMessageDto>>> Messages([FromQuery] int afterId = 0, [FromQuery] int limit = 50, CancellationToken ct = default) =>
        Ok(await queries.MessagesAsync(SellerId, afterId, limit, ct));

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
