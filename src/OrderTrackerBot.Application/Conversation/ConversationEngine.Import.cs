using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// "import": bring customers and products over from an old system. The seller downloads a template (each has a Guide tab), pastes their data in,
// and sends the file back as a WhatsApp document. The bot checks it, shows a summary and saves only after YES. Existing customers (same phone)
// and products (same name + unit) are skipped, never overwritten, so a file can be re-sent safely and "undo" only has new rows to remove.
public partial class ConversationEngine
{
    private const int MaxImportBytes = 5 * 1024 * 1024;
    private const int MaxImportCellLength = 500;
    private const int MaxListedImportErrors = 5;

    private static readonly string[] ImportConfirmButtons = { "Yes", "No" };

    // Row ids are real commands — a tapped row arrives as that text. WhatsApp row titles max out at 24 characters.
    private static readonly IReadOnlyList<MenuSection> ImportTemplateSections = new[]
    {
        new MenuSection("Konsi file chahiye?", new[]
        {
            new MenuRow("import template customers", "👥 Customers file"),
            new MenuRow("import template catalog", "🛍️ Catalog file"),
            new MenuRow("import template all", "📁 Dono (ek file)")
        })
    };

    private static readonly string ImportIntroText =
        MessageTemplates.Get("import.018");

    private Task HandleImportHelpAsync(Seller seller, CancellationToken ct) =>
        _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, ImportIntroText, "File chunein", ImportTemplateSections, ct);

    private async Task HandleImportTemplateAsync(Seller seller, string? kind, CancellationToken ct)
    {
        if (kind is null)
        {
            await HandleImportHelpAsync(seller, ct);
            return;
        }

        if (_exportWriter is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("import.001"), ct);
            return;
        }

        var sheets = DataExchangeGuide.TemplateSheets(kind);
        // The Guide goes first so the file opens on it.
        var workbook = new List<ExportSheet> { DataExchangeGuide.BuildGuideSheet(sheets, template: true) };
        workbook.AddRange(sheets);

        var (fileName, label) = kind switch
        {
            "customers" => ("Import-Customers-Template.xlsx", "Customers"),
            "catalog" => ("Import-Catalog-Template.xlsx", "Catalog"),
            _ => ("Import-Customers-and-Catalog-Template.xlsx", "Customers + Catalog")
        };
        var sent = await _sender.SendDocumentAsync(seller.WhatsAppPhoneNumber, _exportWriter.WriteXlsx(workbook), fileName, XlsxMime,
            $"📥 {label} import file — pehle \"Guide\" sheet parhein", ct);
        await ReplyAsync(seller, sent
            ? MessageTemplates.Get("import.014")
            : MessageTemplates.Get("import.015"), ct);
    }

    /// <summary>An .xlsx the seller sent: check it and ask before saving anything.</summary>
    public async Task HandleDocumentMessageAsync(string fromPhoneNumber, string mediaId, string? fileName, string? mimeType, CancellationToken ct = default)
    {
        _db.MessageLogs.Add(new MessageLog { Phone = fromPhoneNumber, Direction = "inbound", RawText = $"[document {fileName ?? mediaId}]" });
        if (_importReader is null || _media is null)
        {
            await HandleUnsupportedMediaAsync(fromPhoneNumber, "document", ct);
            return;
        }

        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);

        if (!seller.OnboardingComplete)
        {
            // Only at the start of setup and in the catalog step: anywhere else a file would cut across a question the bot just asked.
            if (session.State is not (ConversationState.OnboardingStartChoice or ConversationState.OnboardingCatalogSize or ConversationState.OnboardingAddProduct))
            {
                await ReplyAsync(seller, MessageTemplates.Get("import.002"), ct);
                await PersistAsync(session, ctx, ct);
                return;
            }
        }
        else if (await TryHandleBillingAsync(seller, session, ctx, "", ct))
        {
            await PersistAsync(session, ctx, ct);
            return;
        }
        else if (session.State != ConversationState.Idle)
        {
            await ReplyAsync(seller, MessageTemplates.Get("import.003"), ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        if (!ImportFileTypes.IsSupported(fileName, mimeType))
        {
            await ReplyAsync(seller, MessageTemplates.Get("import.004"), ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        var plan = await LoadImportPlanAsync(seller, mediaId, fileName, ct);
        switch (plan.Problem)
        {
            case ImportProblem.Download:
                await ReplyAsync(seller, MessageTemplates.Get("import.005"), ct);
                break;
            case ImportProblem.TooBig:
                await ReplyAsync(seller, MessageTemplates.Get("import.006"), ct);
                break;
            case ImportProblem.Unreadable:
                await ReplyAsync(seller, MessageTemplates.Get("import.007"), ct);
                break;
            case ImportProblem.NoSheet:
                await ReplyAsync(seller, MessageTemplates.Get("import.008"), ct);
                break;
            case ImportProblem.TooManyRows:
                await ReplyAsync(seller, MessageTemplates.Get("import.009", DataExchangeGuide.MaxImportRows), ct);
                break;
            default:
                await ReplyWithImportSummaryAsync(seller, session, ctx, plan, mediaId, fileName, ct);
                break;
        }

        await PersistAsync(session, ctx, ct);
    }

    private async Task ReplyWithImportSummaryAsync(Seller seller, ConversationSession session, SessionContextData ctx, ImportPlan plan, string mediaId, string? fileName, CancellationToken ct)
    {
        var lines = new List<string> { MessageTemplates.Get("import.019") };
        lines.AddRange(DescribeImportMapping(plan.Mapping));
        if (plan.CustomerRows > 0)
            lines.Add($"👥 Customers: {plan.NewCustomers.Count} naye" + Skipped(plan.ExistingCustomers, plan.FileDuplicateCustomers) + Wrong(plan.CustomerErrors));
        if (plan.ProductRows > 0)
            lines.Add($"🛍️ Products: {plan.NewProducts.Count} naye" + Skipped(plan.ExistingProducts, plan.FileDuplicateProducts) + Wrong(plan.ProductErrors));
        if (plan.VariantNamed > 0) lines.Add(MessageTemplates.Get("import.025", plan.VariantNamed, plan.VariantExample));
        if (plan.DroppedBarcodes > 0) lines.Add(MessageTemplates.Get("import.026", plan.DroppedBarcodes));
        if (plan.Errors.Count > 0)
        {
            lines.Add("");
            lines.Add("⚠️ Yeh rows save nahi hongi (theek kar ke dobara bhej sakte hain):");
            lines.AddRange(plan.Errors.Take(MaxListedImportErrors).Select(e => $"• {e}"));
            if (plan.Errors.Count > MaxListedImportErrors) lines.Add($"• … aur {plan.Errors.Count - MaxListedImportErrors} rows");
        }

        if (!plan.HasNew)
        {
            lines.Add("");
            lines.Add("Save karne ke liye koi nayi row nahi hai.");
            await ReplyAsync(seller, string.Join("\n", lines), ct);
            return;
        }

        ctx.PendingImportMediaId = mediaId;
        ctx.PendingImportFileName = fileName;
        ctx.ImportReturnState = session.State;
        SetState(session, ConversationState.AwaitingImportConfirmation);
        lines.Add("");
        lines.Add("Kya yeh save kar doon?");
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, string.Join("\n", lines), ImportConfirmButtons, ct);

        static string Skipped(int existing, int inFile) =>
            (existing > 0 ? $" · {existing} pehle se maujood (skip)" : "") + (inFile > 0 ? $" · {inFile} file mein dobara (skip)" : "");
        static string Wrong(int errors) => errors > 0 ? $" · {errors} ghalat rows" : "";
    }

    private const int MaxListedIgnoredColumns = 8;

    /// <summary>
    /// Tells the seller how their own layout was read: which of their headings became which field, which columns were left out, and which sheets
    /// held nothing usable. A file that already uses our headings needs none of this and gets no extra lines.
    /// </summary>
    private static IEnumerable<string> DescribeImportMapping(ImportMapping? mapping)
    {
        if (mapping is null) yield break;
        foreach (var sheet in mapping.Sheets)
        {
            var plain = sheet.Ignored.Count == 0 && sheet.Understood.All(u => !u.Guessed && string.Equals(u.Field, u.Source, StringComparison.OrdinalIgnoreCase));
            if (plain) continue;

            yield return "";
            yield return MessageTemplates.Get(sheet.Kind == ImportKind.Customers ? "import.020" : "import.027", sheet.Name);
            yield return MessageTemplates.Get("import.021", string.Join(" · ", sheet.Understood.Select(u => $"{u.Field} ← \"{u.Source}\"")));
            if (sheet.Ignored.Count > 0)
            {
                var listed = string.Join(", ", sheet.Ignored.Take(MaxListedIgnoredColumns).Select(h => $"\"{h}\""));
                yield return MessageTemplates.Get("import.022", sheet.Ignored.Count > MaxListedIgnoredColumns ? $"{listed} +{sheet.Ignored.Count - MaxListedIgnoredColumns}" : listed);
            }
            if (sheet.Understood.FirstOrDefault(u => u.Guessed) is { } guessed)
                yield return MessageTemplates.Get("import.024", guessed.Source);
        }
        foreach (var name in mapping.SkippedSheets)
            yield return MessageTemplates.Get("import.023", name);
        if (mapping.Sheets.Any(s => s.Ignored.Count > 0 || s.Understood.Any(u => u.Guessed)) || mapping.SkippedSheets.Count > 0)
        {
            yield return MessageTemplates.Get("import.029");
            yield return "";
        }
    }

    private async Task HandleImportConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var mediaId = ctx.PendingImportMediaId;
        var fileName = ctx.PendingImportFileName;
        var returnState = ctx.ImportReturnState ?? ConversationState.Idle;
        ctx.PendingImportMediaId = null;
        ctx.PendingImportFileName = null;
        ctx.ImportReturnState = null;
        SetState(session, returnState);

        if (!CommandParser.IsAffirmative(message) || mediaId is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("import.010", (returnState == ConversationState.Idle ? "" : " Jab chahein file dobara bhej dein.")), ct);
            return;
        }

        // The file is read again instead of keeping its rows in the session, and checked against what exists right now.
        var plan = await LoadImportPlanAsync(seller, mediaId, fileName, ct);
        if (plan.Problem != ImportProblem.None || !plan.HasNew)
        {
            await ReplyAsync(seller, plan.Problem == ImportProblem.None
                ? MessageTemplates.Get("import.016")
                : MessageTemplates.Get("import.017"), ct);
            return;
        }

        _db.Customers.AddRange(plan.NewCustomers);
        _db.Products.AddRange(plan.NewProducts);
        await _db.SaveChangesAsync(ct);
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.DataImported,
            PayloadJson = JsonSerializer.Serialize(new
            {
                CustomerIds = plan.NewCustomers.Select(c => c.Id).ToList(),
                ProductIds = plan.NewProducts.Select(p => p.Id).ToList()
            })
        });

        var done = new List<string>();
        if (plan.NewCustomers.Count > 0) done.Add($"👥 {plan.NewCustomers.Count} customers");
        if (plan.NewProducts.Count > 0) done.Add($"🛍️ {plan.NewProducts.Count} products");
        await ReplyAsync(seller,
            MessageTemplates.Get("import.011", string.Join(" · ", done)), ct);

        if (!seller.OnboardingComplete)
        {
            if (returnState == ConversationState.OnboardingStartChoice)
            {
                await AskBusinessNameAsync(seller, session, ct);
            }
            else
            {
                SetState(session, ConversationState.OnboardingAddProduct);
                await ReplyAsync(seller, MessageTemplates.Get("import.012"), ct);
            }
        }
    }

    private async Task UndoDataImportAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(log.PayloadJson);
        List<int> Ids(string name) => doc.RootElement.TryGetProperty(name, out var el) ? el.EnumerateArray().Select(x => x.GetInt32()).ToList() : new();
        var customerIds = Ids("CustomerIds");
        var productIds = Ids("ProductIds");

        // Customers/products that have orders by now are kept out of sight (restorable) instead of orphaning those orders.
        var customers = await _db.Customers.Include(c => c.Orders).Where(c => c.SellerId == seller.Id && customerIds.Contains(c.Id)).ToListAsync(ct);
        foreach (var customer in customers)
        {
            if (customer.Orders.Count == 0) _db.Customers.Remove(customer);
            else customer.DeletedAt = DateTime.UtcNow;
        }

        var usedProductIds = (await _db.OrderItems.Where(i => i.ProductId != null && productIds.Contains(i.ProductId.Value))
            .Select(i => i.ProductId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && productIds.Contains(p.Id)).ToListAsync(ct);
        foreach (var product in products)
        {
            if (usedProductIds.Contains(product.Id)) product.IsActive = false;
            else _db.Products.Remove(product);
        }

        await ReplyAsync(seller, MessageTemplates.Get("import.013", customers.Count, products.Count), ct);
    }

    // ---- reading and checking the file ----

    private enum ImportProblem { None, Download, TooBig, Unreadable, NoSheet, TooManyRows }

    private sealed class ImportPlan
    {
        public ImportProblem Problem;
        public ImportMapping? Mapping;
        public int CustomerRows, ProductRows;
        public List<Customer> NewCustomers = new();
        public List<Product> NewProducts = new();
        public int ExistingCustomers, FileDuplicateCustomers, ExistingProducts, FileDuplicateProducts, CustomerErrors, ProductErrors;
        public int VariantNamed, DroppedBarcodes;
        public string? VariantExample;
        public List<string> Errors = new();
        public bool HasNew => NewCustomers.Count + NewProducts.Count > 0;
    }

    private async Task<ImportPlan> LoadImportPlanAsync(Seller seller, string mediaId, string? fileName, CancellationToken ct)
    {
        var media = await _media!.DownloadAsync(mediaId, ct);
        if (media is null) return new ImportPlan { Problem = ImportProblem.Download };
        if (media.Value.Bytes.Length > MaxImportBytes) return new ImportPlan { Problem = ImportProblem.TooBig };

        var grid = _importReader!.Read(media.Value.Bytes, fileName);
        if (grid is null) return new ImportPlan { Problem = ImportProblem.Unreadable };

        // The seller's own layout is fine: columns are recognised by their headings and renamed to ours before any row is checked.
        var mapping = ImportColumnMapper.Map(grid);
        if (mapping.Sheets.Count == 0) return new ImportPlan { Problem = ImportProblem.NoSheet };
        if (mapping.Sheets.Sum(s => s.Sheet.Rows.Count) > DataExchangeGuide.MaxImportRows)
            return new ImportPlan { Problem = ImportProblem.TooManyRows };

        var plan = new ImportPlan { Mapping = mapping };
        var customerSheets = mapping.Sheets.Where(s => s.Kind == ImportKind.Customers).Select(s => s.Sheet).ToList();
        var catalogSheets = mapping.Sheets.Where(s => s.Kind == ImportKind.Catalog).Select(s => s.Sheet).ToList();
        if (customerSheets.Count > 0) await PlanCustomersAsync(seller, customerSheets, plan, ct);
        if (catalogSheets.Count > 0) await PlanProductsAsync(seller, catalogSheets, plan, ct);
        return plan;
    }

    private static string Cell(ImportRow row, string header) => row.Cells.TryGetValue(header, out var value) ? value : "";

    private static string? TooLong(ImportRow row, params string[] headers) =>
        headers.FirstOrDefault(h => Cell(row, h).Length > MaxImportCellLength) is { } header ? $"\"{header}\" bohat lamba hai" : null;

    private async Task PlanCustomersAsync(Seller seller, IReadOnlyList<ImportSheet> sheets, ImportPlan plan, CancellationToken ct)
    {
        var existing = (await _db.Customers.AsNoTracking().Where(c => c.SellerId == seller.Id && c.DeletedAt == null)
            .Select(c => new { c.Name, c.Phone }).ToListAsync(ct)).Select(c => CustomerImportKey(c.Name, c.Phone)).ToHashSet();
        var seen = new HashSet<string>();

        foreach (var sheet in sheets)
        foreach (var row in sheet.Rows)
        {
            plan.CustomerRows++;
            var name = Cell(row, "Name");
            var phoneText = Cell(row, "Phone");
            string? phone = null;
            string? problem = TooLong(row, "Name", "Phone", "City", "Address", "Preferred contact", "Notes");
            if (problem is null && name.Length == 0) problem = "Name khali hai";
            if (problem is null && phoneText.Length > 0 && (phone = NormalizeImportPhone(phoneText)) is null) problem = $"phone sahi nahi (\"{phoneText}\")";
            if (problem is not null)
            {
                plan.CustomerErrors++;
                plan.Errors.Add($"{sheet.Name} row {row.Number}: {problem}");
                continue;
            }

            var key = CustomerImportKey(name, phone);
            if (existing.Contains(key)) { plan.ExistingCustomers++; continue; }
            if (!seen.Add(key)) { plan.FileDuplicateCustomers++; continue; }

            plan.NewCustomers.Add(new Customer
            {
                SellerId = seller.Id, Name = name, Phone = phone,
                City = NullIfEmpty(Cell(row, "City")), Address = NullIfEmpty(Cell(row, "Address")),
                PreferredContact = NullIfEmpty(Cell(row, "Preferred contact")), Notes = NullIfEmpty(Cell(row, "Notes"))
            });
        }
    }

    private sealed class ImportProductRow
    {
        public required ImportRow Row;
        public required string Name;
        public decimal Price, PackSize = 1;
        public decimal? Cost;
        public int? Stock;
        public string Unit = "piece";
        public bool Active = true;
    }

    private async Task PlanProductsAsync(Seller seller, IReadOnlyList<ImportSheet> sheets, ImportPlan plan, CancellationToken ct)
    {
        var catalog = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id)
            .Select(p => new { p.Name, p.UnitType, p.UnitQty, p.Sku }).ToListAsync(ct);
        var existing = catalog.Select(p => ProductImportKey(p.Name, p.UnitType, p.UnitQty)).ToHashSet();
        var usedBarcodes = catalog.Where(p => !string.IsNullOrWhiteSpace(p.Sku)).Select(p => p.Sku!.Trim().ToLowerInvariant()).ToHashSet();

        var parsed = new List<ImportProductRow>();
        foreach (var sheet in sheets)
        foreach (var row in sheet.Rows)
        {
            plan.ProductRows++;
            var problem = TooLong(row, "Product", "Category", "Vendor", "Manufacturer", "Department", "Size", "Color", "SKU", "Other details");
            var name = Cell(row, "Product");
            decimal price = 0, packSize = 1;
            decimal? cost = null;
            int? stock = null;
            var unit = "piece";
            var active = true;

            if (problem is null && name.Length == 0) problem = "Product ka naam khali hai";
            if (problem is null && !TryImportNumber(Cell(row, "Price"), out price)) problem = Cell(row, "Price").Length == 0 ? "Price khali hai" : $"Price sahi nahi (\"{Cell(row, "Price")}\")";
            if (problem is null && Cell(row, "Cost price").Length > 0)
            {
                if (TryImportNumber(Cell(row, "Cost price"), out var parsedCost)) cost = parsedCost;
                else problem = $"Cost price sahi nahi (\"{Cell(row, "Cost price")}\")";
            }
            if (problem is null && Cell(row, "Unit").Length > 0)
            {
                if (NormalizeImportUnit(Cell(row, "Unit")) is { } parsedUnit) unit = parsedUnit;
                else problem = $"Unit sahi nahi (\"{Cell(row, "Unit")}\")";
            }
            if (problem is null && Cell(row, "Pack size").Length > 0 && (!TryImportNumber(Cell(row, "Pack size"), out packSize) || packSize <= 0)) problem = $"Pack size sahi nahi (\"{Cell(row, "Pack size")}\")";
            if (problem is null && Cell(row, "Stock").Length > 0)
            {
                if (TryImportNumber(Cell(row, "Stock"), out var parsedStock)) stock = (int)Math.Truncate(parsedStock);
                else problem = $"Stock sahi nahi (\"{Cell(row, "Stock")}\")";
            }
            if (problem is null && Cell(row, "Active").Length > 0 && !TryImportYesNo(Cell(row, "Active"), out active)) problem = $"Active mein Yes ya No likhein (\"{Cell(row, "Active")}\")";

            if (problem is not null)
            {
                plan.ProductErrors++;
                plan.Errors.Add($"{sheet.Name} row {row.Number}: {problem}");
                continue;
            }

            parsed.Add(new ImportProductRow { Row = row, Name = name, Price = price, PackSize = packSize, Cost = cost, Stock = stock, Unit = unit, Active = active });
        }

        NameImportVariants(parsed, plan);

        var seen = new HashSet<string>();
        foreach (var item in parsed)
        {
            var row = item.Row;
            var key = ProductImportKey(item.Name, item.Unit, item.PackSize);
            if (existing.Contains(key)) { plan.ExistingProducts++; continue; }
            if (!seen.Add(key)) { plan.FileDuplicateProducts++; continue; }

            // A barcode belongs to one product ("scan" finds a product by it): a repeated one is left off, the product is still saved.
            var barcode = NullIfEmpty(Cell(row, "SKU"));
            if (barcode is not null && !usedBarcodes.Add(barcode.ToLowerInvariant())) { barcode = null; plan.DroppedBarcodes++; }

            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in new[] { "Vendor", "Manufacturer", "Department" })
                if (Cell(row, field).Length > 0) attributes[field] = Cell(row, field);
            foreach (var part in Cell(row, "Other details").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = part.IndexOf(':');
                if (colon > 0 && colon < part.Length - 1) attributes[part[..colon].Trim()] = part[(colon + 1)..].Trim();
            }

            plan.NewProducts.Add(new Product
            {
                SellerId = seller.Id, Name = item.Name, Price = item.Price, CostPrice = item.Cost, UnitType = item.Unit, UnitQty = item.PackSize,
                Category = NullIfEmpty(Cell(row, "Category")), Size = NullIfEmpty(Cell(row, "Size")), Color = NullIfEmpty(Cell(row, "Color")),
                Sku = barcode, StockQty = item.Stock, IsActive = item.Active,
                AttributesJson = attributes.Count == 0 ? null : JsonSerializer.Serialize(attributes)
            });
        }
    }

    /// <summary>
    /// Many sellers list one product on several rows, one per size / colour. The catalog tells products apart by name, so rows that share a name
    /// get their size (and the colour, when the sizes alone don't separate them) added to the name: "Tapal Pouch (S) Red". Rows that still
    /// can't be told apart stay duplicates and are skipped like any repeated row.
    /// </summary>
    private static void NameImportVariants(List<ImportProductRow> rows, ImportPlan plan)
    {
        foreach (var group in rows.GroupBy(r => ProductImportKey(r.Name, r.Unit, r.PackSize)).Where(g => g.Count() > 1))
        {
            var members = group.ToList();
            var sizes = members.Select(r => Cell(r.Row, "Size")).ToList();
            var addColor = sizes.Distinct(StringComparer.OrdinalIgnoreCase).Count() < members.Count;
            foreach (var member in members)
            {
                var variant = string.Join(" ", new[] { Cell(member.Row, "Size"), addColor ? Cell(member.Row, "Color") : "" }.Where(v => v.Length > 0));
                if (variant.Length == 0) continue;
                member.Name = $"{member.Name} {variant}";
                plan.VariantNamed++;
                plan.VariantExample ??= member.Name;
            }
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>Phone numbers lose their leading 0 in Excel (3001234567); +92 / 0092 / 92 forms are written as 0300…. Returns null when it isn't a plausible number.</summary>
    public static string? NormalizeImportPhone(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).Select(c => (char)('0' + (int)char.GetNumericValue(c))).ToArray());
        if (digits.StartsWith("0092")) digits = "0" + digits[4..];
        else if (digits.StartsWith("92") && digits.Length == 12) digits = "0" + digits[2..];
        else if (digits.Length == 10 && digits[0] == '3') digits = "0" + digits;
        return digits.Length is >= 7 and <= 15 ? digits : null;
    }

    /// <summary>Same person = same last 10 digits (however the number was written); without a phone, the same name.</summary>
    private static string CustomerImportKey(string name, string? phone)
    {
        var digits = phone is null ? "" : new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? "n:" + name.Trim().ToLowerInvariant() : "p:" + (digits.Length > 10 ? digits[^10..] : digits);
    }

    private static string ProductImportKey(string name, string unit, decimal unitQty) => $"{name.Trim().ToLowerInvariant()}|{unit}|{unitQty:0.####}";

    private static readonly Regex ImportMoneyNoise = new(@"(?i)\b(?:rs|pkr)\b\.?|,|\s", RegexOptions.Compiled);

    private static bool TryImportNumber(string text, out decimal value)
    {
        value = 0;
        return decimal.TryParse(ImportMoneyNoise.Replace(text, ""), NumberStyles.Number, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static bool TryImportYesNo(string text, out bool value)
    {
        value = true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "yes" or "y" or "true" or "1" or "haan" or "han" or "ha" or "active": return true;
            case "no" or "n" or "false" or "0" or "nahi" or "nahin" or "inactive": value = false; return true;
            default: return false;
        }
    }

    private static string? NormalizeImportUnit(string text) => text.Trim().ToLowerInvariant().TrimEnd('s') switch
    {
        "piece" or "pc" or "pcs" or "pic" => "piece",
        "kg" or "kilo" or "kilogram" => "kg",
        "gram" or "g" or "gm" => "gram",
        "dozen" or "dz" => "dozen",
        "liter" or "litre" or "ltr" or "l" => "liter",
        "meter" or "metre" or "m" => "meter",
        "yard" or "yd" => "yard",
        "pack" or "packet" => "pack",
        "set" => "set",
        "pair" => "pair",
        "ml" or "millilitre" or "milliliter" => "ml",
        "box" => "box",
        "bag" or "bori" => "bag",
        "carton" => "carton",
        "tola" => "tola",
        "ft" or "feet" or "foot" => "ft",
        "roll" => "roll",
        "bundle" => "bundle",
        "tray" => "tray",
        "plate" => "plate",
        _ => null
    };
}
