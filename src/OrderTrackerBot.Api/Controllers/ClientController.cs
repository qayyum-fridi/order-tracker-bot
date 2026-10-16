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
        [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize, [FromQuery] string? stock = null, CancellationToken ct = default)
    {
        if (status is not ("active" or "inactive" or "all")) return BadRequest(new { error = "Status must be active, inactive or all." });
        if (stock is not (null or "" or "tracked" or "low")) return BadRequest(new { error = "Stock must be tracked or low." });
        return Ok(await queries.ProductsAsync(SellerId, search, category, status, page, pageSize, ct, stock));
    }

    [HttpGet("products/{id:int}")]
    public async Task<ActionResult<ProductDto>> Product(int id, CancellationToken ct) =>
        Found(await queries.ProductAsync(SellerId, id, ct), "Product not found.");

    // ----- store settings, payment methods, discounts -----

    [HttpGet("settings")]
    public async Task<ActionResult<SellerSettingsDto>> Settings([FromServices] ClientSettings settings, CancellationToken ct) =>
        Found(await settings.GetAsync(SellerId, ct), "Seller not found.");

    [HttpPut("settings")]
    public async Task<ActionResult<SellerSettingsDto>> SaveSettings([FromBody] SellerSettingsInput input, [FromServices] ClientSettings settings, CancellationToken ct)
    {
        var (problem, saved) = await settings.UpdateAsync(SellerId, input, ct);
        return problem is not null ? BadRequest(new { error = problem }) : Ok(saved);
    }

    [HttpPost("settings/payment-methods")]
    public async Task<ActionResult<PaymentMethodDto>> AddPaymentMethod([FromBody] PaymentMethodInput input, [FromServices] ClientSettings settings, CancellationToken ct)
    {
        var (problem, saved) = await settings.AddPaymentMethodAsync(SellerId, input, ct);
        return problem is not null ? BadRequest(new { error = problem }) : Ok(saved);
    }

    [HttpDelete("settings/payment-methods/{id:int}")]
    public async Task<IActionResult> RemovePaymentMethod(int id, [FromServices] ClientSettings settings, CancellationToken ct) =>
        await settings.RemovePaymentMethodAsync(SellerId, id, ct) ? NoContent() : NotFound(new { error = "Payment method not found." });

    [HttpGet("discounts")]
    public async Task<ActionResult<IReadOnlyList<DiscountDto>>> Discounts([FromServices] ClientDiscounts discounts, CancellationToken ct) =>
        Ok(await discounts.ListAsync(SellerId, ct));

    [HttpPost("discounts")]
    public async Task<ActionResult<DiscountDto>> CreateDiscount([FromBody] DiscountInput input, [FromServices] ClientDiscounts discounts, CancellationToken ct)
    {
        var (problem, saved) = await discounts.CreateAsync(SellerId, input, ct);
        return problem is not null ? BadRequest(new { error = problem }) : Ok(saved);
    }

    [HttpPut("discounts/{id:int}/active")]
    public async Task<ActionResult<DiscountDto>> SetDiscountActive(int id, [FromBody] DiscountActiveInput input, [FromServices] ClientDiscounts discounts, CancellationToken ct) =>
        Found(await discounts.SetActiveAsync(SellerId, id, input.IsActive, ct), "Discount not found.");

    // ----- customers -----

    [HttpPost("customers")]
    public async Task<ActionResult<CustomerDto>> CreateCustomer([FromBody] CustomerInput input, [FromServices] ClientCustomers customers, CancellationToken ct)
    {
        var (problem, saved) = await customers.CreateAsync(SellerId, input, ct);
        return problem is not null ? BadRequest(new { error = problem }) : Ok(saved);
    }

    [HttpPut("customers/{id:int}")]
    public async Task<ActionResult<CustomerDto>> UpdateCustomer(int id, [FromBody] CustomerInput input, [FromServices] ClientCustomers customers, CancellationToken ct)
    {
        var (problem, saved) = await customers.UpdateAsync(SellerId, id, input, ct);
        if (problem is null) return Ok(saved);
        return problem == "Customer not found." ? NotFound(new { error = problem }) : BadRequest(new { error = problem });
    }

    [HttpDelete("customers/{id:int}")]
    public async Task<IActionResult> DeleteCustomer(int id, [FromServices] ClientCustomers customers, CancellationToken ct) =>
        await customers.DeleteAsync(SellerId, id, ct) ? NoContent() : NotFound(new { error = "Customer not found." });

    // ----- orders and categories -----

    /// <summary>Moves one order to Shipped, Delivered, Cancelled or Returned. Refused moves are a 409 with the reason.</summary>
    [HttpPut("orders/{id:int}/status")]
    public async Task<ActionResult<OrderDetailDto>> SetOrderStatus(int id, [FromBody] OrderStatusInput input, [FromServices] ClientOrderStatus orders, CancellationToken ct)
    {
        var (problem, saved) = await orders.ChangeAsync(SellerId, id, input.Status, ct);
        if (problem is null) return Ok(saved);
        return problem == "Order not found." ? NotFound(new { error = problem }) : Conflict(new { error = problem });
    }

    [HttpPut("categories/rename")]
    public async Task<ActionResult<object>> RenameCategory([FromBody] CategoryRenameInput input, [FromServices] ClientCategoryRename rename, CancellationToken ct)
    {
        var (problem, updated) = await rename.RenameAsync(SellerId, input, ct);
        return problem is not null ? BadRequest(new { error = problem }) : Ok(new { updated });
    }

    // ----- CSV downloads -----

    /// <summary>CSV of orders, order-items, customers or catalog. Orders and items use from/to (Pakistan days); the default is the last 30 days.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? dataset, [FromQuery] string? format, [FromQuery] string? from, [FromQuery] string? to,
        [FromServices] ClientExport export, CancellationToken ct)
    {
        if (!ClientExport.Datasets.Contains(dataset)) return BadRequest(new { error = "Choose orders, order-items, customers or catalog." });
        if (format is not (null or "" or "csv")) return BadRequest(new { error = "Only CSV is available here." });
        if (!AdminDateRange.TryResolve(from, to, 30, out var range, out var error)) return BadRequest(new { error });
        var (fileName, content) = await export.BuildAsync(SellerId, dataset!, range, ct);
        return File(System.Text.Encoding.UTF8.GetBytes(content), "text/csv; charset=utf-8", fileName);
    }

    /// <summary>Sets the stock count of a tracked product (one with a stock number).</summary>
    [HttpPut("products/{id:int}/stock")]
    public async Task<ActionResult<ProductStockDto>> SetStock(int id, [FromBody] StockInput input, [FromServices] ClientStock stock, CancellationToken ct)
    {
        var (problem, saved) = await stock.SetAsync(SellerId, id, input, ct);
        if (problem is null) return Ok(saved);
        return problem == "Product not found." ? NotFound(new { error = problem }) : BadRequest(new { error = problem });
    }

    // ----- money out and stock write-offs (dates are Pakistan days, default the last 30) -----

    [HttpGet("expenses")]
    public async Task<ActionResult<MoneyListDto<ExpenseDto>>> Expenses([FromQuery] string? from, [FromQuery] string? to, [FromServices] ClientExpenses expenses, CancellationToken ct)
    {
        if (!AdminDateRange.TryResolve(from, to, 30, out var range, out var error)) return BadRequest(new { error });
        return Ok(await expenses.ListAsync(SellerId, range, ct));
    }

    [HttpPost("expenses")]
    public async Task<ActionResult<ExpenseDto>> CreateExpense([FromBody] ExpenseInput input, [FromServices] ClientExpenses expenses, CancellationToken ct)
    {
        var (problem, saved) = await expenses.CreateAsync(SellerId, input, ct);
        return problem is not null ? BadRequest(new { error = problem }) : Ok(saved);
    }

    [HttpDelete("expenses/{id:int}")]
    public async Task<IActionResult> DeleteExpense(int id, [FromServices] ClientExpenses expenses, CancellationToken ct) =>
        await expenses.DeleteAsync(SellerId, id, ct) ? NoContent() : NotFound(new { error = "Expense not found." });

    [HttpGet("wages")]
    public async Task<ActionResult<MoneyListDto<WageDto>>> Wages([FromQuery] string? from, [FromQuery] string? to, [FromServices] ClientWages wages, CancellationToken ct)
    {
        if (!AdminDateRange.TryResolve(from, to, 30, out var range, out var error)) return BadRequest(new { error });
        return Ok(await wages.ListAsync(SellerId, range, ct));
    }

    [HttpPost("wages")]
    public async Task<ActionResult<WageDto>> CreateWage([FromBody] WageInput input, [FromServices] ClientWages wages, CancellationToken ct)
    {
        var (problem, saved) = await wages.CreateAsync(SellerId, input, ct);
        return problem is not null ? BadRequest(new { error = problem }) : Ok(saved);
    }

    [HttpDelete("wages/{id:int}")]
    public async Task<IActionResult> DeleteWage(int id, [FromServices] ClientWages wages, CancellationToken ct) =>
        await wages.DeleteAsync(SellerId, id, ct) ? NoContent() : NotFound(new { error = "Wage entry not found." });

    [HttpGet("losses")]
    public async Task<ActionResult<MoneyListDto<LossDto>>> Losses([FromQuery] string? from, [FromQuery] string? to, [FromServices] ClientLosses losses, CancellationToken ct)
    {
        if (!AdminDateRange.TryResolve(from, to, 30, out var range, out var error)) return BadRequest(new { error });
        return Ok(await losses.ListAsync(SellerId, range, ct));
    }

    /// <summary>Writes off stock. Tracked products lose the units; the cost is copied from the product.</summary>
    [HttpPost("losses")]
    public async Task<ActionResult<LossDto>> CreateLoss([FromBody] LossInput input, [FromServices] ClientLosses losses, CancellationToken ct)
    {
        var (problem, saved) = await losses.CreateAsync(SellerId, input, ct);
        return problem is not null ? BadRequest(new { error = problem }) : Ok(saved);
    }

    /// <summary>Creates a sale from the dashboard. Prices come from the catalog; stock and the undo log are updated like WhatsApp orders.</summary>
    [HttpPost("orders")]
    public async Task<ActionResult<OrderDetailDto>> CreateOrder([FromBody] NewOrderInput input, [FromServices] ClientOrderCreate orders, CancellationToken ct)
    {
        var (problem, saved) = await orders.CreateAsync(SellerId, input, ct);
        return problem is not null ? BadRequest(new { error = problem }) : Ok(saved);
    }

    /// <summary>Lists the photos of a product, oldest first. The bytes come from the image route.</summary>
    [HttpGet("products/{id:int}/images")]
    public async Task<ActionResult<IReadOnlyList<ProductImageDto>>> ProductImages(int id, [FromServices] ClientProductImages images, CancellationToken ct) =>
        Ok(await images.ListAsync(SellerId, id, ct));

    /// <summary>Uploads one photo (multipart field "file"): JPG, PNG or WebP, 2 MB at most.</summary>
    [HttpPost("products/{id:int}/images")]
    [RequestSizeLimit(ClientProductImages.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<object>> UploadProductImage(int id, IFormFile? file, [FromServices] ClientProductImages images, CancellationToken ct)
    {
        if (file is null) return BadRequest(new { error = "Choose a photo to upload." });
        if (file.Length > ClientProductImages.MaxBytes) return BadRequest(new { error = "Photos can be 2 MB at most." });
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var (problem, imageId) = await images.AddAsync(SellerId, id, buffer.ToArray(), ct);
        if (problem is not null) return BadRequest(new { error = problem });
        return Ok(new { id = imageId });
    }

    /// <summary>The photo itself, for the dashboard to show.</summary>
    [HttpGet("products/{id:int}/images/{imageId:int}")]
    public async Task<IActionResult> ProductImageFile(int id, int imageId, [FromServices] ClientProductImages images, CancellationToken ct)
    {
        var image = await images.GetAsync(SellerId, id, imageId, ct);
        if (image is null) return NotFound(new { error = "Photo not found." });
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(image.Data, image.ContentType);
    }

    [HttpDelete("products/{id:int}/images/{imageId:int}")]
    public async Task<IActionResult> DeleteProductImage(int id, int imageId, [FromServices] ClientProductImages images, CancellationToken ct) =>
        await images.DeleteAsync(SellerId, id, imageId, ct) ? NoContent() : NotFound(new { error = "Photo not found." });

    /// <summary>Adds a product. A blank SKU gets an automatic one.</summary>
    [HttpPost("products")]
    public async Task<ActionResult<ProductDto>> CreateProduct([FromBody] ClientProductInput input, [FromServices] ClientProductWriter writer, CancellationToken ct) =>
        await SavedProduct(await writer.CreateAsync(SellerId, input, ct), ct);

    /// <summary>Replaces a product's details. A field left empty is cleared.</summary>
    [HttpPut("products/{id:int}")]
    public async Task<ActionResult<ProductDto>> UpdateProduct(int id, [FromBody] ClientProductInput input, [FromServices] ClientProductWriter writer, CancellationToken ct) =>
        await SavedProduct(await writer.UpdateAsync(SellerId, id, input, ct), ct);

    /// <summary>Hides, shows or sets the category of several products. Returns how many changed.</summary>
    [HttpPost("products/bulk")]
    public async Task<ActionResult<object>> BulkProducts([FromBody] ClientProductBulkInput input, [FromServices] ClientProductBulk bulk, CancellationToken ct)
    {
        var problem = ClientProductBulk.Validate(input);
        if (problem is not null) return BadRequest(new { error = problem });
        return Ok(new { updated = await bulk.BulkAsync(SellerId, input, ct) });
    }

    [HttpPost("orders/bulk-status")]
    public async Task<ActionResult<object>> BulkOrderStatus([FromBody] ClientOrderBulkInput input, [FromServices] ClientOrderBulk bulk, CancellationToken ct)
    {
        var problem = ClientOrderBulk.Validate(input);
        if (problem is not null) return BadRequest(new { error = problem });
        var result = await bulk.BulkStatusAsync(SellerId, input, ct);
        return Ok(new { updated = result.Updated, skipped = result.Skipped });
    }

    [HttpPost("customers/bulk-price-group")]
    public async Task<ActionResult<object>> BulkCustomerPriceGroup([FromBody] ClientCustomerBulkInput input, [FromServices] ClientCustomerBulk bulk, CancellationToken ct)
    {
        var problem = ClientCustomerBulk.Validate(input);
        if (problem is not null) return BadRequest(new { error = problem });
        return Ok(new { updated = await bulk.BulkPriceGroupAsync(SellerId, input, ct) });
    }

    /// <summary>Sends a receipt, a customer card, a report summary or an export to the shop's own WhatsApp chat.</summary>
    [HttpPost("send-whatsapp")]
    public async Task<ActionResult<object>> SendToWhatsApp([FromBody] DashboardSendRequest input, [FromServices] ConversationEngine engine, CancellationToken ct)
    {
        var result = await engine.SendFromDashboardAsync(SellerId, input, ct);
        if (!result.Sent) return BadRequest(new { error = SendReasonText(result.Reason) });
        return Ok(new { sent = true, message = SendReasonText(result.Reason) });
    }

    private static string SendReasonText(string reason) => reason switch
    {
        "receipt-sent" => "Receipt sent to the shop's WhatsApp.",
        "customer-sent" => "Customer card sent to the shop's WhatsApp.",
        "report-sent" => "Report summary sent to the shop's WhatsApp.",
        "export-sent" => "Export sent to the shop's WhatsApp.",
        "export-needs-dataset" => "Choose orders, customers or catalog to export.",
        "no-receipt-pdf" => "Receipt PDFs aren't available on this bot yet.",
        "order-not-found" => "Order not found.",
        "customer-not-found" => "Customer not found.",
        "bad-range" => "Choose a start and end date, with the end on or after the start (at most a year).",
        "send-failed" => "WhatsApp did not accept the file. The shop must message the bot within the last 24 hours, then try again.",
        _ => "Unknown send request.",
    };

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
