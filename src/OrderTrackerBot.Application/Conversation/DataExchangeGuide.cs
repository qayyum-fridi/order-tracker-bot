using OrderTrackerBot.Application.Abstractions;

namespace OrderTrackerBot.Application.Conversation;

public enum GuideLevel { Required, Recommended, Optional, Auto, Info }

public sealed record GuideColumn(string Header, Type Type, GuideLevel Level, string Help, string Example);

public sealed record GuideSheetSpec(string Sheet, bool Importable, string Summary, IReadOnlyList<GuideColumn> Columns);

/// <summary>
/// One description of every exported sheet: what each column holds and how to fill it. It feeds the "Guide" tab that every export and
/// import template carries, and the import templates' headers (the columns the importer actually reads), so the three can't drift apart.
/// </summary>
public static class DataExchangeGuide
{
    public const string GuideSheetName = "Guide";
    public const string CustomersSheet = "Customers";
    public const string CatalogSheet = "Catalog";
    public const int MaxImportRows = 5000;

    private static GuideColumn C(string header, Type type, GuideLevel level, string help, string example) => new(header, type, level, help, example);

    private static readonly GuideSheetSpec Customers = new(CustomersSheet, true,
        "Aap ke customers. Import mein har row ek customer banati hai. / Your customers — each row becomes one customer.", new[]
    {
        C("Name", typeof(string), GuideLevel.Required, "Customer ka naam. / Customer name.", "Ayesha Khan"),
        C("Phone", typeof(string), GuideLevel.Recommended, "Mobile number. Isi se customer pehchana jata hai: same phone = same customer (dobara nahi banega). Shuru ka 0 ghayab ho jaye to bot khud theek kar leta hai. / Used to recognise a customer — same phone means same customer.", "03001234567"),
        C("City", typeof(string), GuideLevel.Optional, "Shehar. / City.", "Lahore"),
        C("Address", typeof(string), GuideLevel.Optional, "Delivery address. / Delivery address.", "House 12, Street 4, Gulberg"),
        C("Preferred contact", typeof(string), GuideLevel.Optional, "Kaise rabta karna pasand hai (marzi). / How the customer likes to be contacted.", "whatsapp"),
        C("Notes", typeof(string), GuideLevel.Optional, "Koi bhi yaad-dasht. / Any note.", "Shaam ko call karein"),
        C("Orders", typeof(int), GuideLevel.Auto, "Is customer ke orders ki ginti — bot khud hisaab lagata hai. / Order count, calculated by the bot.", "3"),
        C("Total spent", typeof(decimal), GuideLevel.Auto, "Is customer ne kul kitna kharch kiya — khud banta hai. / Total spent, calculated.", "12500"),
        C("Last order", typeof(DateTime), GuideLevel.Auto, "Akhri order ki tareekh — khud banti hai. / Date of the last order, calculated.", "2026-09-30"),
        C("Customer since", typeof(DateTime), GuideLevel.Auto, "Customer kab add hua — khud banta hai. / Date added, calculated.", "2026-01-15")
    });

    private static readonly GuideSheetSpec Catalog = new(CatalogSheet, true,
        "Aap ke products. Import mein har row ek product banati hai. / Your products — each row becomes one product.", new[]
    {
        C("Product", typeof(string), GuideLevel.Required, "Product ka naam. Same naam + same unit dobara nahi banega. / Product name; the same name + unit is not created twice.", "Lawn Suit"),
        C("Price", typeof(decimal), GuideLevel.Required, "Bechne ki qeemat (sirf number, Rs. na likhein). / Selling price, number only.", "3500"),
        C("Cost price", typeof(decimal), GuideLevel.Optional, "Aap ko kitne ka para — sirf aap ko dikhta hai, profit report mein kaam aata hai. / What it costs you; private, used for profit reports.", "2400"),
        C("Unit", typeof(string), GuideLevel.Optional, "piece, kg, gram, dozen, liter, meter, yard ya pack. Khali = piece. / One of piece, kg, gram, dozen, liter, meter, yard, pack. Blank = piece.", "piece"),
        C("Pack size", typeof(decimal), GuideLevel.Optional, "Ek listing mein kitni unit: \"Sugar 5kg\" ke liye Unit = kg, Pack size = 5. Khali = 1. / Units in one listing. Blank = 1.", "1"),
        C("Category", typeof(string), GuideLevel.Optional, "Zumra / category. / Category.", "Stitched"),
        C("Vendor", typeof(string), GuideLevel.Optional, "Supplier ka naam. / Supplier name.", "Ali Traders"),
        C("Manufacturer", typeof(string), GuideLevel.Optional, "Banane wala / brand. / Manufacturer or brand.", "Gul Ahmed"),
        C("Department", typeof(string), GuideLevel.Optional, "Department. / Department.", "Women"),
        C("Size", typeof(string), GuideLevel.Optional, "Size. / Size.", "M"),
        C("Color", typeof(string), GuideLevel.Optional, "Rang. / Colour.", "Blue"),
        C("SKU", typeof(string), GuideLevel.Optional, "Aap ka apna product code. / Your own product code.", "LS-001"),
        C("Other details", typeof(string), GuideLevel.Optional, "Koi aur tafseel \"naam: value\" ki shakal mein, ek se zyada ho to ; laga kar. / Extra details as name: value, separated by ;", "fabric: cotton; season: summer"),
        C("Stock", typeof(int), GuideLevel.Optional, "Abhi kitna maal hai. Likhne par bot stock ginna shuru kar deta hai, khali chhorein to stock track nahi hoga. / Quantity on hand. Filling it turns stock tracking on for that product.", "20"),
        C("Active", typeof(string), GuideLevel.Optional, "Yes ya No. Khali = Yes. / Yes or No. Blank = Yes.", "Yes"),
        C("Added on", typeof(DateTime), GuideLevel.Auto, "Product kab add hua — khud banta hai. / Date added, calculated.", "2026-01-15")
    });

    private static readonly GuideSheetSpec Orders = new(
        "Orders", false,
        "Aap ke orders ka record (backup / hisaab ke liye). Orders abhi import nahi hote. / Your order record, for backup and accounts. Orders can't be imported yet.", new[]
    {
        C("Order #", typeof(int), GuideLevel.Info, "Order ka number.", "12"),
        C("Date", typeof(DateTime), GuideLevel.Info, "Order ki tareekh (aap ke time mein).", "2026-09-30 14:05"),
        C("Customer", typeof(string), GuideLevel.Info, "Customer ka naam.", "Ayesha Khan"),
        C("Phone", typeof(string), GuideLevel.Info, "Customer ka phone.", "03001234567"),
        C("Address", typeof(string), GuideLevel.Info, "Address aur shehar.", "House 12, Gulberg, Lahore"),
        C("Items", typeof(string), GuideLevel.Info, "Items: \"quantity x product\".", "2 x Lawn Suit; 1 x Dupatta"),
        C("Subtotal", typeof(decimal), GuideLevel.Info, "Discount aur delivery se pehle ka total.", "7500"),
        C("Discount", typeof(decimal), GuideLevel.Info, "Discount ki raqam.", "500"),
        C("Discount code", typeof(string), GuideLevel.Info, "Istemal hua discount code.", "EID10"),
        C("Delivery", typeof(decimal), GuideLevel.Info, "Delivery charges.", "200"),
        C("Total", typeof(decimal), GuideLevel.Info, "Kul raqam (delivery shamil).", "7200"),
        C("Status", typeof(string), GuideLevel.Info, "Pending / Shipped / Delivered / Cancelled / Returned.", "Shipped"),
        C("Payment status", typeof(string), GuideLevel.Info, "Paid ya Unpaid.", "Unpaid"),
        C("Amount paid", typeof(decimal), GuideLevel.Info, "Ab tak mili raqam.", "1000"),
        C("Balance", typeof(decimal), GuideLevel.Info, "Baqi raqam.", "6200"),
        C("Payment method", typeof(string), GuideLevel.Info, "COD / Transfer / Online.", "COD"),
        C("Paid on", typeof(DateTime), GuideLevel.Info, "Payment ki tareekh.", "2026-10-02"),
        C("Courier", typeof(string), GuideLevel.Info, "Courier ka naam.", "TCS"),
        C("Tracking #", typeof(string), GuideLevel.Info, "Tracking number.", "TCS123456"),
        C("Source", typeof(string), GuideLevel.Info, "Order kahan se aaya.", "Instagram"),
        C("Delivery date", typeof(DateTime), GuideLevel.Info, "Delivery ki tareekh.", "2026-10-05"),
        C("Notes", typeof(string), GuideLevel.Info, "Order ka note.", "Gift wrap"),
        C("Receipt #", typeof(int), GuideLevel.Info, "Receipt ka number.", "12"),
        C("Sales tax %", typeof(decimal), GuideLevel.Info, "Sales tax ka percent.", "16"),
        C("Sales tax", typeof(decimal), GuideLevel.Info, "Sales tax ki raqam.", "1000"),
        C("Tax withheld", typeof(decimal), GuideLevel.Info, "Kata hua (withheld) tax.", "0"),
        C("Net after withholding", typeof(decimal), GuideLevel.Info, "Withholding ke baad ki raqam.", "7200")
    });

    private static readonly GuideSheetSpec OrderItems = new(
        "Order Items", false,
        "Har order ke items alag alag rows mein. / Each order's items, one row per item.", new[]
    {
        C("Order #", typeof(int), GuideLevel.Info, "Kis order ka item hai.", "12"),
        C("Customer", typeof(string), GuideLevel.Info, "Customer ka naam.", "Ayesha Khan"),
        C("Product", typeof(string), GuideLevel.Info, "Product ka naam.", "Lawn Suit"),
        C("Quantity", typeof(int), GuideLevel.Info, "Kitne.", "2"),
        C("Unit price", typeof(decimal), GuideLevel.Info, "Ek ki qeemat.", "3500"),
        C("Line total", typeof(decimal), GuideLevel.Info, "Quantity x qeemat.", "7000")
    });

    private static readonly GuideSheetSpec Discounts = new(
        "Discounts", false,
        "Aap ke discount codes (backup ke liye). Abhi import nahi hote — bot mein \"create discount: ...\" se banayein. / Your discount codes; not importable yet.", new[]
    {
        C("Code", typeof(string), GuideLevel.Info, "Discount code.", "EID10"),
        C("Type", typeof(string), GuideLevel.Info, "Percent ya Flat (Rs.).", "Percent"),
        C("Value", typeof(decimal), GuideLevel.Info, "Percent ya raqam.", "10"),
        C("Expires", typeof(DateTime), GuideLevel.Info, "Kab khatam hota hai.", "2026-12-31"),
        C("Active", typeof(string), GuideLevel.Info, "Yes ya No.", "Yes"),
        C("Created", typeof(DateTime), GuideLevel.Info, "Kab bana.", "2026-09-01")
    });

    private static readonly GuideSheetSpec LoyaltyRules = new(
        "Loyalty Rules", false,
        "Loyalty discount ke rules (backup ke liye). Abhi import nahi hote. / Loyalty rules; not importable yet.", new[]
    {
        C("After this many orders", typeof(int), GuideLevel.Info, "Itne orders ke baad discount milta hai.", "5"),
        C("Discount %", typeof(decimal), GuideLevel.Info, "Discount ka percent.", "10"),
        C("Active", typeof(string), GuideLevel.Info, "Yes ya No.", "Yes")
    });

    private static readonly Dictionary<string, GuideSheetSpec> BySheet =
        new[] { Customers, Catalog, Orders, OrderItems, Discounts, LoyaltyRules }.ToDictionary(s => s.Sheet, StringComparer.OrdinalIgnoreCase);

    public static GuideSheetSpec? SpecFor(string sheetName) => BySheet.GetValueOrDefault(sheetName);

    /// <summary>Columns the importer reads for a sheet: everything except the calculated (Auto) ones.</summary>
    public static IReadOnlyList<GuideColumn> ImportColumns(string sheetName) =>
        SpecFor(sheetName) is { Importable: true } spec ? spec.Columns.Where(c => c.Level != GuideLevel.Auto).ToList() : Array.Empty<GuideColumn>();

    /// <summary>The empty, ready-to-fill workbook sheets for "customers", "catalog" or "all" (headers only — no example rows that could be imported by mistake).</summary>
    public static List<ExportSheet> TemplateSheets(string kind)
    {
        var sheets = new List<ExportSheet>();
        if (kind is "customers" or "all") sheets.Add(Empty(CustomersSheet));
        if (kind is "catalog" or "all") sheets.Add(Empty(CatalogSheet));
        return sheets;

        static ExportSheet Empty(string name) =>
            new(name, ImportColumns(name).Select(c => new ExportColumn(c.Header, c.Type)).ToList(), new List<object?[]>());
    }

    private static string LevelText(GuideLevel level) => level switch
    {
        GuideLevel.Required => "REQUIRED / Zaroori",
        GuideLevel.Recommended => "Recommended / Behtar hai",
        GuideLevel.Optional => "Optional / Marzi",
        GuideLevel.Auto => "Auto - leave it / Khud banta hai",
        _ => "Info only / Sirf dekhne ke liye"
    };

    private const string UrduHeader = "Kya likhna hai";
    private const string EnglishHeader = "What to write";
    private static readonly string[] GuideHeaders = { "Sheet", "Column", "Needed?", UrduHeader, EnglishHeader, "Example" };
    private const int WrapAt = 90;

    /// <summary>Cells don't wrap in the exported file, so long text is cut into short lines that go on following rows.</summary>
    private static List<string> WrapText(string text)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > WrapAt) { lines.Add(line); line = word; }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>
    /// The "Guide" tab for a workbook holding <paramref name="contentSheets"/>: how to use the file, then every column of every sheet in it
    /// (taken from the sheets themselves, so the tab always matches the file). <paramref name="template"/> picks the "fill and send back" wording.
    /// Help text is written "Roman Urdu / English" and lands in two columns.
    /// </summary>
    public static ExportSheet BuildGuideSheet(IReadOnlyList<ExportSheet> contentSheets, bool template)
    {
        var rows = new List<object?[]>();

        void Entry(string sheet, string column, string needed, string help, string example)
        {
            var parts = help.Split(" / ", 2);
            var urdu = WrapText(parts[0]);
            var english = parts.Length > 1 ? WrapText(parts[1]) : new List<string>();
            for (var i = 0; i < Math.Max(1, Math.Max(urdu.Count, english.Count)); i++)
                rows.Add(new object?[]
                {
                    i == 0 ? sheet : "", i == 0 ? column : "", i == 0 ? needed : "",
                    i < urdu.Count ? urdu[i] : "", i < english.Count ? english[i] : "", i == 0 ? example : ""
                });
        }

        var anyImportable = contentSheets.Any(s => SpecFor(s.Name) is { Importable: true });
        if (template)
        {
            Entry("Step 1", "", "", "Har sheet ki pehli row (headings) ko na badlein, na sheet ka naam, na koi column delete karein. / Don't rename the sheets or change the heading row.", "");
            Entry("Step 2", "", "", "Apne purane system ya copy se data copy kar ke dusri row se paste karein — ek row = ek customer ya ek product. / Paste your old data from row 2 down, one row per customer or product.", "");
            Entry("Step 3", "", "", "Neeche 'Needed?' column dekhein: REQUIRED wale columns khali na chhorein, baqi marzi hain. / Required columns must be filled; the rest are optional.", "");
            Entry("Step 4", "", "", "File ko Excel Workbook (.xlsx) ke taur par save karein — CSV ya .xls nahi chalta. / Save as .xlsx only.", "");
            Entry("Step 5", "", "", "File isi WhatsApp chat mein bhej dein. Bot pehle summary dikhata hai (kitne naye, kitne skip, kisi row mein ghalti); aap YES kahenge tab hi save hota hai, aur \"undo\" se wapas ho jata hai. / Send the file back here; nothing is saved until you reply YES, and \"undo\" reverses it.", "");
            Entry("Note", "", "", "Jo pehle se maujood hai (same phone wala customer, same naam wala product) dobara nahi banega — skip ho jata hai. Ek file mein zyada se zyada " + MaxImportRows + " rows aur 5 MB. / Existing customers (same phone) and products (same name) are skipped, not duplicated. Max " + MaxImportRows + " rows, 5 MB.", "");
        }
        else if (anyImportable)
        {
            Entry("About", "", "", "Yeh file aap ke data ki copy (backup) hai. Customers aur Catalog sheets dobara import ho sakti hain; baqi sheets sirf dekhne ke liye hain. / This file is a copy of your data. The Customers and Catalog sheets can be imported again; the other sheets are for reference.", "");
            Entry("Move data", "", "", "Naye account mein laane ke liye: bot ko \"import\" likhein ya yeh file seedha bhej dein — sirf Customers aur Catalog sheets parhi jati hain, baqi ignore hoti hain. / To bring this into another account, send this file to the bot; only Customers and Catalog are read.", "");
            Entry("Auto columns", "", "", "'Auto' wale columns bot khud hisaab se banata hai; import mein ignore hote hain. / Columns marked Auto are calculated and ignored on import.", "");
        }
        else
        {
            Entry("About", "", "", "Yeh file aap ke data ki copy hai — hisaab aur backup ke liye. Is sheet ko abhi import nahi kiya ja sakta. / A copy of your data for accounts and backup; these sheets can't be imported yet.", "");
        }

        foreach (var sheet in contentSheets)
        {
            var spec = SpecFor(sheet.Name);
            rows.Add(new object?[] { "", "", "", "", "", "" });
            Entry(sheet.Name, "", "", spec?.Summary ?? "", "");
            foreach (var column in sheet.Columns)
            {
                var col = spec?.Columns.FirstOrDefault(c => string.Equals(c.Header, column.Header, StringComparison.OrdinalIgnoreCase));
                if (col is null) Entry(sheet.Name, column.Header, "", "", "");
                else Entry(sheet.Name, col.Header, LevelText(col.Level), col.Help, col.Example);
            }
        }

        return new ExportSheet(GuideSheetName, GuideHeaders.Select(h => new ExportColumn(h, typeof(string))).ToList(), rows);
    }
}
