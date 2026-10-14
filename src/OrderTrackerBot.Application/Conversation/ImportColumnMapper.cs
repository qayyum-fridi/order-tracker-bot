using System.Text;
using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Application.Conversation;

public enum ImportKind { Customers, Catalog }

/// <summary>One column the mapper understood: the bot's heading, the heading it found in the seller's file, and whether it was guessed from the cell values (not from the heading).</summary>
public sealed record ImportColumnUse(string Field, string Source, bool Guessed);

public sealed record MappedImportSheet(string Name, ImportKind Kind, ImportSheet Sheet, IReadOnlyList<ImportColumnUse> Understood, IReadOnlyList<string> Ignored);

/// <param name="SkippedSheets">Sheets that held neither a customer list nor a product list the mapper could recognise.</param>
public sealed record ImportMapping(IReadOnlyList<MappedImportSheet> Sheets, IReadOnlyList<string> SkippedSheets);

/// <summary>
/// Lets a seller import a file in their own layout: finds the heading row (it may sit under a title), recognises columns by their heading
/// (English / Roman Urdu / Urdu script, typos like "Catagory", extra words like "Variation size"), decides whether a sheet is a customer list or a
/// product list, and renames what it understood to the bot's own headings so the normal import checks run unchanged. Plain string rules, no AI.
/// A column it can't place is left out and reported, never guessed into a field; the one value-based guess is the phone column of a customer list.
/// </summary>
public static class ImportColumnMapper
{
    private const int HeaderSearchRows = 10;
    private const int MaxHeaderLength = 30;
    private const int PhoneSampleRows = 200;
    private const double PhoneShare = 0.6;

    private enum F
    {
        Name, CustomerName, ProductName, Phone, City, Address, PreferredContact, Notes,
        Price, CostPrice, Unit, PackSize, Stock, Category, Vendor, Manufacturer, Department, Size, Color, Sku, Active, OtherDetails
    }

    // Headings the importer reads, in the order they are listed back to the seller.
    private static readonly (F Field, string Heading)[] CustomerFields =
    {
        (F.Name, "Name"), (F.Phone, "Phone"), (F.City, "City"), (F.Address, "Address"), (F.PreferredContact, "Preferred contact"), (F.Notes, "Notes")
    };

    private static readonly (F Field, string Heading)[] CatalogFields =
    {
        (F.ProductName, "Product"), (F.Price, "Price"), (F.CostPrice, "Cost price"), (F.Unit, "Unit"), (F.PackSize, "Pack size"), (F.Category, "Category"),
        (F.Vendor, "Vendor"), (F.Manufacturer, "Manufacturer"), (F.Department, "Department"), (F.Size, "Size"), (F.Color, "Color"), (F.Sku, "SKU"),
        (F.OtherDetails, "Other details"), (F.Stock, "Stock"), (F.Active, "Active")
    };

    private static readonly Dictionary<string, F> Exact = BuildExact();

    private static Dictionary<string, F> BuildExact()
    {
        var map = new Dictionary<string, F>();
        void Add(F field, params string[] words) { foreach (var word in words) map.TryAdd(Norm(word), field); }

        Add(F.Sku, "sku", "code", "item code", "product code", "barcode", "barcodes", "bar code", "upc", "ean", "item no", "item number", "product no", "product number", "item id", "product id", "کوڈ", "بارکوڈ");
        Add(F.CostPrice, "cost", "cost price", "purchase", "purchase price", "purchase rate", "buy", "buy price", "buying price", "buy rate", "kharid", "kharid price", "kharidi", "لاگت", "خرید");
        Add(F.Price, "price", "rate", "mrp", "rs", "pkr", "rupees", "unit price", "sale price", "sales price", "selling price", "sell price", "retail price", "qeemat", "qimat", "kimat", "daam", "dam", "قیمت", "ریٹ");
        Add(F.PackSize, "pack size", "pack", "pack of", "per pack", "units per pack");
        Add(F.Stock, "stock", "qty", "quantity", "stock qty", "inventory", "in stock", "on hand", "available", "available qty", "mojood", "mojud", "اسٹاک", "تعداد", "موجود");
        Add(F.Unit, "unit", "units", "uom", "unit type", "unit of measure", "measure", "sold by");
        Add(F.Phone, "phone", "phone no", "phone number", "mobile", "mobile no", "mobile number", "mob", "mob no", "cell", "cell no", "cell number", "contact", "contact no", "contact number",
            "whatsapp", "whatsapp no", "whatsapp number", "number", "fone", "فون", "فون نمبر", "موبائل", "موبائل نمبر", "نمبر", "رابطہ");
        Add(F.City, "city", "shehar", "sheher", "town", "شہر");
        Add(F.Address, "address", "pata", "patta", "location", "پتہ", "پتا", "ایڈریس");
        Add(F.PreferredContact, "preferred contact", "preferred");
        Add(F.Notes, "note", "notes", "remark", "remarks", "comment", "comments", "نوٹ", "نوٹس", "ریمارکس");
        Add(F.Category, "category", "categories", "cat", "group", "class", "zumra", "qism", "زمرہ", "قسم");
        Add(F.Department, "department", "dept");
        Add(F.Vendor, "vendor", "supplier", "vendor name", "supplier name", "سپلائر");
        Add(F.Manufacturer, "manufacturer", "brand", "make", "maker", "company", "برانڈ");
        Add(F.Size, "size", "sizes", "سائز");
        Add(F.Color, "color", "colour", "rang", "shade", "رنگ");
        Add(F.Active, "active", "is active");
        Add(F.OtherDetails, "other details", "details", "extra", "attributes", "specs", "specifications");
        Add(F.ProductName, "product", "products", "product name", "item", "items", "item name", "mal", "maal", "cheez", "chiz", "title", "پروڈکٹ", "آئٹم", "مال");
        Add(F.CustomerName, "customer", "customer name", "client", "client name", "buyer", "grahak", "gahak", "full name", "گاہک", "کسٹمر");
        Add(F.Name, "name", "naam", "nam", "نام");
        return map;
    }

    /// <summary>Lower-case letters and digits only, with the Arabic look-alikes of Urdu letters folded together ("Mobile No." -> "mobileno").</summary>
    private static string Norm(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            var c = ch switch { 'ي' or 'ى' => 'ی', 'ك' => 'ک', 'ه' or 'ة' or 'ۃ' => 'ہ', _ => ch };
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool Has(string normalized, params string[] parts) => parts.Any(p => normalized.Contains(Norm(p), StringComparison.Ordinal));

    /// <summary>What a heading cell stands for, or null. Whole-heading matches first, then words inside longer headings; the order of the rules is the priority.</summary>
    private static F? Classify(string heading)
    {
        var n = Norm(heading);
        if (n.Length == 0 || n.Length > MaxHeaderLength) return null;
        if (Exact.TryGetValue(n, out var exact)) return exact;

        if (Has(n, "sku", "barcode", "itemcode", "productcode", "itemno", "productno", "itemnumber", "productnumber", "itemid", "productid", "بارکوڈ")) return F.Sku;
        if (Has(n, "preferred")) return F.PreferredContact;
        if (Has(n, "cost", "purchase", "buying", "kharid", "khareed", "lagat", "لاگت", "خرید")) return F.CostPrice;
        if (!Has(n, "discount", "tax", "commission", "percent", "profit", "margin", "total", "delivery", "order", "date", "wholesale") &&
            (Has(n, "price", "qeemat", "qimat", "kimat", "daam", "mrp", "selling", "قیمت", "ریٹ") || n.StartsWith("rate", StringComparison.Ordinal) || n.EndsWith("rate", StringComparison.Ordinal)))
            return F.Price;
        if (Has(n, "packsize", "packof", "perpack")) return F.PackSize;
        if (!Has(n, "pack", "order", "sold") && Has(n, "stock", "qty", "quantity", "inventory", "onhand", "mojood", "mojud", "اسٹاک", "تعداد", "موجود")) return F.Stock;
        if (Has(n, "uom", "unittype", "unitof", "measure")) return F.Unit;
        if (Has(n, "phone", "mobile", "whatsapp", "contactno", "contactnumber", "fone", "فون", "موبائل")) return F.Phone;
        if ((n.Length <= 14 && (n.StartsWith("city", StringComparison.Ordinal) || (n.EndsWith("city", StringComparison.Ordinal) && !n.EndsWith("capacity", StringComparison.Ordinal)))) ||
            Has(n, "shehar", "sheher", "شہر")) return F.City;
        if (Has(n, "address", "location", "street", "ایڈریس")) return F.Address;
        if (Has(n, "note", "remark", "comment", "نوٹ")) return F.Notes;
        if (Has(n, "categ", "catag", "catog", "katag", "zumra", "زمرہ")) return F.Category;
        if (Has(n, "department")) return F.Department;
        if (Has(n, "vendor", "supplier", "سپلائر")) return F.Vendor;
        if (Has(n, "manufactur", "brand")) return F.Manufacturer;
        if (Has(n, "color", "colour", "رنگ")) return F.Color;
        if (Has(n, "size", "سائز")) return F.Size;
        if (Has(n, "customer", "client", "buyer", "grahak", "gahak", "کسٹمر", "گاہک")) return F.CustomerName;
        if (Has(n, "product", "itemname")) return F.ProductName;
        return null;
    }

    private static readonly HashSet<string> AutoHeaders = new[] { DataExchangeGuide.CustomersSheet, DataExchangeGuide.CatalogSheet }
        .Select(DataExchangeGuide.SpecFor).Where(s => s is not null).SelectMany(s => s!.Columns)
        .Where(c => c.Level == GuideLevel.Auto).Select(c => Norm(c.Header)).ToHashSet();

    public static ImportMapping Map(IReadOnlyList<ImportGridSheet> sheets)
    {
        var mapped = new List<MappedImportSheet>();
        var skipped = new List<string>();
        foreach (var sheet in sheets)
        {
            // Our own Guide tab and the export-only tabs (Orders, Discounts, ...) are never import data.
            if (sheet.Name.Equals(DataExchangeGuide.GuideSheetName, StringComparison.OrdinalIgnoreCase) ||
                DataExchangeGuide.SpecFor(sheet.Name) is { Importable: false }) continue;

            if (TryMap(sheet) is { } result) mapped.Add(result);
            else skipped.Add(sheet.Name);
        }
        return new ImportMapping(mapped, skipped);
    }

    private static string Cell(ImportGridRow row, int column) => column < row.Cells.Count ? row.Cells[column] : "";

    private static MappedImportSheet? TryMap(ImportGridSheet sheet)
    {
        // The heading row: the one of the first rows that names the most columns we know (a title above it names at most one).
        var headerIndex = -1;
        var bestScore = 1;
        for (var i = 0; i < Math.Min(HeaderSearchRows, sheet.Rows.Count); i++)
        {
            var score = sheet.Rows[i].Cells.Count(c => c.Length > 0 && Classify(c) is not null);
            if (score > bestScore) { headerIndex = i; bestScore = score; }
        }
        if (headerIndex < 0) return null;

        var header = sheet.Rows[headerIndex];
        var data = sheet.Rows.Skip(headerIndex + 1).ToList();

        // field -> first column that holds it
        var byField = new Dictionary<F, int>();
        for (var col = 0; col < header.Cells.Count; col++)
            if (header.Cells[col].Length > 0 && Classify(header.Cells[col]) is { } field) byField.TryAdd(field, col);

        var hasPerson = byField.ContainsKey(F.Name) || byField.ContainsKey(F.CustomerName);
        var guessedPhone = -1;
        if (hasPerson && !byField.ContainsKey(F.Price) && !byField.ContainsKey(F.Phone))
        {
            guessedPhone = FindPhoneColumn(data, header.Cells.Count, byField.Values.ToHashSet());
            if (guessedPhone >= 0) byField[F.Phone] = guessedPhone;
        }

        var hasContact = byField.ContainsKey(F.Phone) || byField.ContainsKey(F.City) || byField.ContainsKey(F.Address);
        var catalogOk = byField.ContainsKey(F.Price) && (byField.ContainsKey(F.ProductName) || byField.ContainsKey(F.Name));
        var customersOk = hasPerson && hasContact;

        ImportKind kind;
        if (sheet.Name.Equals(DataExchangeGuide.CustomersSheet, StringComparison.OrdinalIgnoreCase) && hasPerson) kind = ImportKind.Customers;
        else if (sheet.Name.Equals(DataExchangeGuide.CatalogSheet, StringComparison.OrdinalIgnoreCase) && catalogOk) kind = ImportKind.Catalog;
        else if (catalogOk) kind = ImportKind.Catalog;
        else if (customersOk) kind = ImportKind.Customers;
        else return null;

        var uses = new List<(string Heading, int Column, bool Guessed)>();
        foreach (var (field, heading) in kind == ImportKind.Customers ? CustomerFields : CatalogFields)
        {
            var found = byField.TryGetValue(field, out var column);
            if (!found && field == F.Name && byField.TryGetValue(F.CustomerName, out column)) found = true;
            if (!found && field == F.ProductName && byField.TryGetValue(F.Name, out column)) found = true;
            if (found) uses.Add((heading, column, field == F.Phone && column == guessedPhone));
        }

        var rows = new List<ImportRow>();
        foreach (var row in data)
        {
            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (heading, column, _) in uses)
                if (Cell(row, column) is { Length: > 0 } value) cells[heading] = value;
            if (cells.Count > 0) rows.Add(new ImportRow(row.Number, cells));
        }

        var used = uses.Select(u => u.Column).ToHashSet();
        var ignored = new List<string>();
        for (var col = 0; col < header.Cells.Count; col++)
        {
            var text = header.Cells[col];
            if (text.Length == 0 || used.Contains(col) || AutoHeaders.Contains(Norm(text))) continue;
            if (data.Any(r => Cell(r, col).Length > 0)) ignored.Add(text);
        }

        var understood = uses.Select(u => new ImportColumnUse(u.Heading, header.Cells[u.Column], u.Guessed)).ToList();
        return new MappedImportSheet(sheet.Name, kind, new ImportSheet(sheet.Name, rows), understood, ignored);
    }

    /// <summary>The unclaimed column most of whose values look like mobile numbers, or -1. Used only when a customer list has no phone heading we know.</summary>
    private static int FindPhoneColumn(IReadOnlyList<ImportGridRow> data, int width, HashSet<int> claimed)
    {
        var best = -1;
        var bestShare = PhoneShare;
        for (var col = 0; col < width; col++)
        {
            if (claimed.Contains(col)) continue;
            var values = data.Take(PhoneSampleRows).Select(r => Cell(r, col)).Where(v => v.Length > 0).ToList();
            if (values.Count == 0) continue;
            var share = values.Count(LooksLikePhone) / (double)values.Count;
            if (share >= bestShare) { best = col; bestShare = share; }
        }
        return best;
    }

    private static bool LooksLikePhone(string value) =>
        !value.Any(char.IsLetter) && value.Count(char.IsDigit) is >= 10 and <= 13 && ConversationEngine.NormalizeImportPhone(value) is not null;
}
