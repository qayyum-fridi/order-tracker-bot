using System.Globalization;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>One item row of a daily sales file, after its cells were read and checked (not yet matched to the catalog).</summary>
public sealed record SalesImportLine(int Row, string Product, int Quantity, decimal? Price);

/// <summary>One order in the file: the rows that share a day and a customer.</summary>
public sealed record SalesImportOrder(
    int FirstRow, DateOnly Day, string? CustomerName, string? Phone, string Payment, string Status, List<SalesImportLine> Lines);

public sealed record SalesImportRowError(int Row, string Message);

public sealed record SalesImportParse(
    string? Problem,
    IReadOnlyList<ClientImportColumn> MappedColumns,
    IReadOnlyList<string> IgnoredHeadings,
    int RowCount,
    IReadOnlyList<SalesImportOrder> Orders,
    IReadOnlyList<SalesImportRowError> Errors);

/// <summary>
/// Reads a daily sales spreadsheet: one row per item, rows of the same customer and day form one order. Columns are
/// found by their headings (English, loosely matched). Pure parsing: nothing here touches the database.
/// Expected headings: Date, Customer, Phone, Product, Quantity, Price, Payment, Status. Product and Quantity are
/// required, and Customer or Phone too. Price, Date, Payment and Status are optional.
/// </summary>
public static class SalesImportPlanner
{
    public const int MaxRows = 5000;
    public const int MaxQuantity = 999;
    private const int HeaderSearchRows = 10;

    private static readonly (string Field, string[] Words)[] Fields =
    [
        ("Product", ["product", "item", "sku name"]),
        ("Quantity", ["quantity", "qty", "pcs", "units"]),
        ("Price", ["unit price", "price", "rate"]),
        ("Customer", ["customer", "buyer", "client", "name"]),
        ("Phone", ["phone", "mobile", "whatsapp", "contact"]),
        ("Date", ["date", "day"]),
        ("Payment", ["payment", "pay method"]),
        ("Status", ["status"]),
    ];

    public static SalesImportParse Parse(IReadOnlyList<ImportGridSheet> sheets, DateOnly today)
    {
        foreach (var sheet in sheets)
        {
            var header = FindHeader(sheet.Rows);
            if (header is null) continue;
            return ParseRows(sheet, header.Value.Index, header.Value.Columns, today);
        }
        return new SalesImportParse("NoSales", [], [], 0, [], []);
    }

    private static (int Index, Dictionary<string, int> Columns)? FindHeader(IReadOnlyList<ImportGridRow> rows)
    {
        for (var i = 0; i < Math.Min(rows.Count, HeaderSearchRows); i++)
        {
            var columns = MapHeadings(rows[i].Cells);
            if (columns.ContainsKey("Product") && columns.ContainsKey("Quantity") && (columns.ContainsKey("Customer") || columns.ContainsKey("Phone")))
                return (i, columns);
        }
        return null;
    }

    private static Dictionary<string, int> MapHeadings(IReadOnlyList<string> cells)
    {
        var columns = new Dictionary<string, int>();
        for (var c = 0; c < cells.Count; c++)
        {
            var heading = cells[c].Trim().ToLowerInvariant();
            if (heading.Length == 0) continue;
            foreach (var (field, words) in Fields)
            {
                if (columns.ContainsKey(field)) continue;
                if (words.Any(w => heading == w || heading.Contains(w)) && !(field == "Price" && heading.Contains("total")))
                {
                    columns[field] = c;
                    break;
                }
            }
        }
        return columns;
    }

    private static SalesImportParse ParseRows(ImportGridSheet sheet, int headerIndex, Dictionary<string, int> columns, DateOnly today)
    {
        var headings = sheet.Rows[headerIndex].Cells;
        var mapped = columns.Select(kv => new ClientImportColumn(headings[kv.Value].Trim(), kv.Key)).ToList();
        var ignored = headings.Select((h, c) => (h, c)).Where(x => x.h.Trim().Length > 0 && !columns.ContainsValue(x.c)).Select(x => x.h.Trim()).ToList();

        var dataRows = sheet.Rows.Skip(headerIndex + 1).Where(r => r.Cells.Any(c => c.Trim().Length > 0)).ToList();
        if (dataRows.Count > MaxRows) return new SalesImportParse("TooManyRows", mapped, ignored, dataRows.Count, [], []);

        var errors = new List<SalesImportRowError>();
        var orders = new Dictionary<string, SalesImportOrder>();
        foreach (var row in dataRows)
        {
            var parsed = ParseRow(row, columns, today, out var error);
            if (error is not null)
            {
                errors.Add(new SalesImportRowError(row.Number, error));
                continue;
            }
            var (day, name, phone, payment, status, line) = parsed!.Value;
            var key = $"{day:yyyy-MM-dd}|{(phone is null ? "n:" + name?.ToLowerInvariant() : "p:" + LastTen(phone))}";
            if (!orders.TryGetValue(key, out var order))
            {
                order = new SalesImportOrder(row.Number, day, name, phone, payment, status, []);
                orders[key] = order;
            }
            else if (order.Payment != payment || order.Status != status)
            {
                errors.Add(new SalesImportRowError(row.Number, $"payment or status differs from row {order.FirstRow} of the same order. Use the same values on every row of one order."));
                continue;
            }
            order.Lines.Add(line);
        }
        return new SalesImportParse(null, mapped, ignored, dataRows.Count, orders.Values.ToList(), errors);
    }

    private static (DateOnly Day, string? Name, string? Phone, string Payment, string Status, SalesImportLine Line)? ParseRow(
        ImportGridRow row, Dictionary<string, int> columns, DateOnly today, out string? error)
    {
        error = null;
        string Cell(string field) => columns.TryGetValue(field, out var c) && c < row.Cells.Count ? row.Cells[c].Trim() : "";

        var product = Cell("Product");
        if (product.Length == 0) { error = "product is missing."; return null; }

        if (!int.TryParse(Cell("Quantity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity < 1 || quantity > MaxQuantity)
        {
            error = $"quantity must be a whole number from 1 to {MaxQuantity}.";
            return null;
        }

        decimal? price = null;
        var priceText = Cell("Price");
        if (priceText.Length > 0)
        {
            if (!decimal.TryParse(priceText.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedPrice) || parsedPrice < 0)
            {
                error = $"price \"{priceText}\" is not a number.";
                return null;
            }
            price = parsedPrice;
        }

        var day = today;
        var dateText = Cell("Date");
        if (dateText.Length > 0 && !TryParseDay(dateText, out day))
        {
            error = $"date \"{dateText}\" is not a day. Use 2026-10-16 or 16/10/2026.";
            return null;
        }
        if (day > today) { error = "date is in the future."; return null; }

        var name = Cell("Customer");
        var phoneText = Cell("Phone");
        string? phone = null;
        if (phoneText.Length > 0)
        {
            phone = ConversationEngine.NormalizeImportPhone(phoneText);
            if (phone is null) { error = $"phone \"{phoneText}\" is not a valid number."; return null; }
        }
        if (name.Length == 0 && phone is null) { error = "customer name or phone is missing."; return null; }

        var payment = NormalizePayment(Cell("Payment"));
        if (payment is null) { error = "payment must be COD, Bank, Easypaisa, JazzCash, Card or Gateway."; return null; }
        var status = NormalizeStatus(Cell("Status"));
        if (status is null) { error = "status must be Pending, Shipped or Delivered."; return null; }

        return (day, name.Length == 0 ? null : name, phone, payment, status, new SalesImportLine(row.Number, product, quantity, price));
    }

    /// <summary>Excel may hand back a day as text in several forms, or as its serial number.</summary>
    internal static bool TryParseDay(string text, out DateOnly day)
    {
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd-MMM-yyyy", "d MMM yyyy", "yyyy/MM/dd"];
        if (DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return true;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 2958466)
        {
            day = DateOnly.FromDateTime(DateTime.FromOADate(serial));
            return true;
        }
        return false;
    }

    internal static string? NormalizePayment(string text) => text.Trim().ToLowerInvariant() switch
    {
        "" or "cod" or "cash" or "cash on delivery" => "Cod",
        "bank" or "bank transfer" or "manual" or "easypaisa" or "jazzcash" or "jazz cash" or "wallet" => "Manual",
        "card" or "gateway" or "online" => "Gateway",
        _ => null,
    };

    internal static string? NormalizeStatus(string text) => text.Trim().ToLowerInvariant() switch
    {
        "" or "delivered" => "Delivered",
        "pending" => "Pending",
        "shipped" => "Shipped",
        _ => null,
    };

    private static string LastTen(string phone) => phone.Length > 10 ? phone[^10..] : phone;
}
