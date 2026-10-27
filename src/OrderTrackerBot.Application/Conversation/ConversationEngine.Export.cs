using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// "export": the seller picks what they want (orders / customers / catalog / discounts / everything) and gets one Excel file
// in their own chat. Excel (.xlsx) rather than CSV: Urdu text, leading-zero phone numbers and several sheets survive.
public partial class ConversationEngine
{
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Row ids are real commands — a tapped row arrives as that text. WhatsApp row titles max out at 24 characters.
    private static readonly IReadOnlyList<MenuSection> ExportSections = new[]
    {
        new MenuSection("Kya export karna hai?", new[]
        {
            new MenuRow("export orders", "📦 Orders (sab)"),
            new MenuRow("export orders 30 days", "📦 Orders (30 din)"),
            new MenuRow("export orders last month", "📦 Orders (pichla maah)"),
            new MenuRow("export customers", "👥 Customers"),
            new MenuRow("export catalog", "🛍️ Catalog"),
            new MenuRow("export discounts", "🎟️ Discounts + loyalty"),
            new MenuRow("export all", "📁 Sab kuch (ek file)")
        })
    };

    private async Task HandleExportAsync(Seller seller, ExportRequest request, CancellationToken ct)
    {
        if (_exportWriter is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("export.001"), ct);
            return;
        }

        if (request.Datasets.Count == 0)
        {
            await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber,
                MessageTemplates.Get("export.002"), "Options dekhein", ExportSections, ct);
            return;
        }

        var file = await BuildExportAsync(seller, request, ct);
        if (file.Bytes is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("export.003", file.Summary), ct);
            return;
        }
        var sent = await _sender.SendDocumentAsync(seller.WhatsAppPhoneNumber, file.Bytes, file.FileName, XlsxMime,
            $"📊 Export — {file.Summary}", ct);
        await ReplyAsync(seller, sent
            ? MessageTemplates.Get("export.004")
            : MessageTemplates.Get("export.005"), ct);
    }

    /// <summary>
    /// The Excel file for one export request, shared by the WhatsApp send and "email export". <c>Bytes</c> is null when no dataset has rows
    /// (<c>Summary</c> then says what was looked at). Needs the export writer, which the callers check first.
    /// </summary>
    private async Task<(byte[]? Bytes, string FileName, string Summary)> BuildExportAsync(Seller seller, ExportRequest request, CancellationToken ct)
    {
        var tz = SellerClock.Resolve(seller.TimeZoneId);
        DateTime? Local(DateTime? utc) => utc is null ? null : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), tz);

        var sheets = new List<ExportSheet>();
        var summary = new List<string>();
        var anyRows = false;
        foreach (var dataset in request.Datasets)
        {
            var built = dataset switch
            {
                "orders" => await BuildOrderSheetsAsync(seller, request.Period, Local, ct),
                "customers" => await BuildCustomerSheetsAsync(seller, Local, ct),
                "catalog" => await BuildCatalogSheetsAsync(seller, Local, ct),
                "expenses" => await BuildExpenseSheetsAsync(seller, request.Period, Local, ct),
                _ => await BuildDiscountSheetsAsync(seller, Local, ct)
            };
            sheets.AddRange(built.Sheets);
            summary.Add($"{built.Label} ({built.Sheets[0].Rows.Count})");
            anyRows |= built.Sheets.Any(s => s.Rows.Count > 0);
        }

        if (!anyRows) return (null, "", string.Join(", ", summary));

        // The Guide tab goes last so the file opens on the data; it explains every column of the sheets above.
        sheets.Add(DataExchangeGuide.BuildGuideSheet(sheets.ToList(), template: false));
        var bytes = _exportWriter!.WriteXlsx(sheets);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var fileName = $"{SafeFileStem(seller.BusinessName)}-export-{today:yyyy-MM-dd}.xlsx";
        return (bytes, fileName, string.Join(", ", summary));
    }

    private static string SafeFileStem(string? businessName)
    {
        var stem = Regex.Replace(businessName ?? "", @"[^A-Za-z0-9]+", "-").Trim('-');
        return stem.Length == 0 ? "OrderTracker" : stem.Length > 40 ? stem[..40] : stem;
    }

    private static ExportColumn Col(string header, Type type) => new(header, type);

    private async Task<(string Label, List<ExportSheet> Sheets)> BuildOrderSheetsAsync(Seller seller, string? period, Func<DateTime?, DateTime?> local, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var (start, end) = period switch
        {
            "today" or "yesterday" or "lastmonth" => ReportRange(seller, period),
            not null when ReportPeriods.Resolve(period, seller.TimeZoneId, now) is { } resolved && period is not ("7d" or "30d") => (resolved.StartUtc, resolved.EndUtc),
            "7d" => (SellerClock.LocalDayRangeUtc(seller.TimeZoneId, now, -6).StartUtc, DateTime.MaxValue),
            "30d" => (SellerClock.LocalDayRangeUtc(seller.TimeZoneId, now, -29).StartUtc, DateTime.MaxValue),
            _ => (DateTime.MinValue, DateTime.MaxValue)
        };
        var label = period switch
        {
            "today" => "Orders (aaj)", "yesterday" => "Orders (kal)", "lastmonth" => MessageTemplates.Get("export.006"),
            "7d" => "Orders (7 din)", "30d" => "Orders (30 din)",
            not null when ReportPeriods.Resolve(period, seller.TimeZoneId, now) is not null => $"Orders ({PeriodLabel(seller, period).ToLowerInvariant()})",
            _ => "Orders"
        };

        var orders = await _db.Orders.AsNoTracking().Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end)
            .OrderBy(o => o.CreatedAt).ToListAsync(ct);

        var orderSheet = new ExportSheet("Orders", new[]
        {
            Col("Order #", typeof(int)), Col("Date", typeof(DateTime)), Col("Customer", typeof(string)), Col("Phone", typeof(string)),
            Col("Address", typeof(string)), Col("Items", typeof(string)), Col("Subtotal", typeof(decimal)), Col("Discount", typeof(decimal)),
            Col("Discount code", typeof(string)), Col("Delivery", typeof(decimal)), Col("Total", typeof(decimal)), Col("Status", typeof(string)), Col("Payment status", typeof(string)), Col("Amount paid", typeof(decimal)), Col("Balance", typeof(decimal)),
            Col("Payment method", typeof(string)), Col("Paid on", typeof(DateTime)), Col("Courier", typeof(string)), Col("Tracking #", typeof(string)),
            Col("Source", typeof(string)), Col("Delivery date", typeof(DateTime)), Col("Notes", typeof(string)),
            Col("Receipt #", typeof(int)), Col("Sales tax %", typeof(decimal)), Col("Sales tax", typeof(decimal)),
            Col("Tax withheld", typeof(decimal)), Col("Net after withholding", typeof(decimal)), Col("Courier fee", typeof(decimal))
        }, orders.Select(o => new object?[]
        {
            o.Id, local(o.CreatedAt), o.Customer?.Name, o.Customer?.Phone,
            string.Join(", ", new[] { o.Customer?.Address, o.Customer?.City }.Where(s => !string.IsNullOrWhiteSpace(s))),
            string.Join("; ", o.Items.Select(i => $"{i.Quantity} x {i.ProductNameSnapshot}")), o.Subtotal, o.DiscountAmount,
            o.DiscountCode, o.DeliveryCharge, o.Total, Formatters.Status(o.Status), o.PaymentStatus.ToString(), OrderMoney.Received(o), OrderMoney.Balance(o),
            o.PaymentMethod switch { OrderPaymentMethod.Cod => "COD", OrderPaymentMethod.Manual => "Transfer", OrderPaymentMethod.Gateway => "Online", _ => "" },
            local(o.PaidAt), o.TrackingCourier, o.TrackingNumber, o.OrderSource, local(o.DeliveryDate), o.Notes,
            o.ReceiptNumber, o.SalesTaxRate, SalesTax.Amount(o), o.TaxWithheld, SalesTax.NetOfWithheld(o), o.CourierFee
        }).ToList());

        var itemSheet = new ExportSheet("Order Items", new[]
        {
            Col("Order #", typeof(int)), Col("Customer", typeof(string)), Col("Product", typeof(string)),
            Col("Quantity", typeof(int)), Col("Unit price", typeof(decimal)), Col("Line total", typeof(decimal))
        }, orders.SelectMany(o => o.Items.Select(i => new object?[] { o.Id, o.Customer?.Name, i.ProductNameSnapshot, i.Quantity, i.UnitPrice, i.LineTotal })).ToList());

        return (label, new List<ExportSheet> { orderSheet, itemSheet });
    }

    /// <summary>Expenses in the export's period (same period words as the orders sheet; default = all time). Courier fees are on the Orders sheet.</summary>
    private async Task<(string Label, List<ExportSheet> Sheets)> BuildExpenseSheetsAsync(Seller seller, string? period, Func<DateTime?, DateTime?> local, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var (start, end) = period switch
        {
            "today" or "yesterday" or "lastmonth" or "7d" or "30d" => ReportRange(seller, period),
            not null when ReportPeriods.Resolve(period, seller.TimeZoneId, now) is { } resolved => (resolved.StartUtc, resolved.EndUtc),
            _ => (DateTime.MinValue, DateTime.MaxValue)
        };
        var label = period is null ? "Expenses" : $"Expenses ({PeriodLabel(seller, period).ToLowerInvariant()})";

        var expenses = await _db.Expenses.AsNoTracking()
            .Where(x => x.SellerId == seller.Id && x.CreatedAt >= start && x.CreatedAt < end)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);
        var sheet = new ExportSheet("Expenses", new[]
        {
            Col("Date", typeof(DateTime)), Col("Category", typeof(string)), Col("Note", typeof(string)), Col("Amount", typeof(decimal))
        }, expenses.Select(x => new object?[] { local(x.CreatedAt), x.Category, x.Note, x.Amount }).ToList());
        return (label, new List<ExportSheet> { sheet });
    }

    private async Task<(string Label, List<ExportSheet> Sheets)> BuildCustomerSheetsAsync(Seller seller, Func<DateTime?, DateTime?> local, CancellationToken ct)
    {
        var customers = await _db.Customers.AsNoTracking()
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null).OrderBy(c => c.Name).ToListAsync(ct);
        // Aggregated in memory: Sqlite can't SUM decimal columns server-side.
        var totals = (await _db.Orders.AsNoTracking()
                .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
                .Select(o => new { o.CustomerId, o.Total, o.CreatedAt }).ToListAsync(ct))
            .GroupBy(o => o.CustomerId)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Spent: g.Sum(o => o.Total), Last: g.Max(o => o.CreatedAt)));

        var sheet = new ExportSheet("Customers", new[]
        {
            Col("Name", typeof(string)), Col("Phone", typeof(string)), Col("City", typeof(string)), Col("Address", typeof(string)),
            Col("Preferred contact", typeof(string)), Col("Notes", typeof(string)), Col("Orders", typeof(int)),
            Col("Total spent", typeof(decimal)), Col("Last order", typeof(DateTime)), Col("Customer since", typeof(DateTime))
        }, customers.Select(c =>
        {
            totals.TryGetValue(c.Id, out var t);
            return new object?[] { c.Name, c.Phone, c.City, c.Address, c.PreferredContact, c.Notes, t.Count, t.Spent,
                t.Count == 0 ? null : local(t.Last), local(c.CreatedAt) };
        }).ToList());
        return ("Customers", new List<ExportSheet> { sheet });
    }

    private async Task<(string Label, List<ExportSheet> Sheets)> BuildCatalogSheetsAsync(Seller seller, Func<DateTime?, DateTime?> local, CancellationToken ct)
    {
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id).OrderBy(p => p.Id).ToListAsync(ct);
        var sheet = new ExportSheet("Catalog", new[]
        {
            Col("Product", typeof(string)), Col("Price", typeof(decimal)), Col("Cost price", typeof(decimal)), Col("Unit", typeof(string)), Col("Pack size", typeof(decimal)),
            Col("Category", typeof(string)), Col("Vendor", typeof(string)), Col("Manufacturer", typeof(string)), Col("Department", typeof(string)),
            Col("Size", typeof(string)), Col("Color", typeof(string)), Col("SKU", typeof(string)), Col("Other details", typeof(string)),
            Col("Stock", typeof(int)), Col("Active", typeof(string)), Col("Added on", typeof(DateTime))
        }, products.Select(p => new object?[] { p.Name, p.Price, p.CostPrice, p.UnitType, p.UnitQty, p.Category,
            ProductDetail(p, "vendor"), ProductDetail(p, "manufacturer"), ProductDetail(p, "department"), p.Size, p.Color, p.Sku, OtherProductDetails(p),
            p.StockQty, p.IsActive ? "Yes" : "No", local(p.CreatedAt) }).ToList());
        return ("Catalog", new List<ExportSheet> { sheet });
    }

    private async Task<(string Label, List<ExportSheet> Sheets)> BuildDiscountSheetsAsync(Seller seller, Func<DateTime?, DateTime?> local, CancellationToken ct)
    {
        var discounts = await _db.Discounts.AsNoTracking().Where(d => d.SellerId == seller.Id).OrderBy(d => d.Id).ToListAsync(ct);
        var loyalty = await _db.LoyaltyRules.AsNoTracking().Where(l => l.SellerId == seller.Id).OrderBy(l => l.OrderThreshold).ToListAsync(ct);
        var discountSheet = new ExportSheet("Discounts", new[]
        {
            Col("Code", typeof(string)), Col("Type", typeof(string)), Col("Value", typeof(decimal)), Col("Expires", typeof(DateTime)),
            Col("Active", typeof(string)), Col("Created", typeof(DateTime))
        }, discounts.Select(d => new object?[] { d.Code, d.Type == DiscountType.Percent ? "Percent" : "Flat (Rs.)", d.Value,
            local(d.ExpiresAt), d.IsActive ? "Yes" : "No", local(d.CreatedAt) }).ToList());
        var loyaltySheet = new ExportSheet("Loyalty Rules", new[]
        {
            Col("After this many orders", typeof(int)), Col("Discount %", typeof(decimal)), Col("Active", typeof(string))
        }, loyalty.Select(l => new object?[] { l.OrderThreshold, l.DiscountPercent, l.IsActive ? "Yes" : "No" }).ToList());
        return ("Discounts", new List<ExportSheet> { discountSheet, loyaltySheet });
    }
}
