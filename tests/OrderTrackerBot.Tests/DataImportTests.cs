using Microsoft.EntityFrameworkCore;
using MiniExcelLibs;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Export;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class DataImportTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly Mock<IWhatsAppMediaClient> _media = new();
    private readonly List<string> _texts = new();
    private readonly List<string> _buttonBodies = new();
    private readonly List<(string Body, IReadOnlyList<MenuSection> Sections)> _lists = new();
    private readonly List<(string File, byte[] Bytes, string? Caption)> _files = new();
    private readonly ExportXlsxWriter _writer = new();

    public DataImportTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _texts.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _buttonBodies.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, sections, _) => _lists.Add((body, sections))).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], string, string, string?, CancellationToken>((_, bytes, name, _, caption, _) => _files.Add((name, bytes, caption))).ReturnsAsync(true);
    }

    public void Dispose() => _dbFactory.Dispose();

    private ConversationEngine CreateEngine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, _founderAlerts.Object, media: _media.Object, exportWriter: _writer, importReader: new ImportXlsxReader());

    private async Task OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Lawn Suit - 3500", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        ClearOutput();
    }

    private void ClearOutput()
    {
        _texts.Clear();
        _buttonBodies.Clear();
        _lists.Clear();
        _files.Clear();
    }

    private void ServeFile(string mediaId, byte[] bytes) =>
        _media.Setup(m => m.DownloadAsync(mediaId, It.IsAny<CancellationToken>())).ReturnsAsync((bytes, XlsxMime));

    private byte[] Workbook(IEnumerable<object?[]>? customers, IEnumerable<object?[]>? products)
    {
        var sheets = new List<ExportSheet>();
        if (customers is not null)
            sheets.Add(new ExportSheet("Customers", new[] { "Name", "Phone", "City", "Address" }.Select(h => new ExportColumn(h, typeof(string))).ToList(), customers.ToList()));
        if (products is not null)
            sheets.Add(new ExportSheet("Catalog", new[]
            {
                new ExportColumn("Product", typeof(string)), new ExportColumn("Price", typeof(string)), new ExportColumn("Cost price", typeof(string)),
                new ExportColumn("Unit", typeof(string)), new ExportColumn("Stock", typeof(string)), new ExportColumn("Vendor", typeof(string)),
                new ExportColumn("Other details", typeof(string))
            }, products.ToList()));
        return _writer.WriteXlsx(sheets);
    }

    private static object?[] Row(params object?[] cells) => cells;

    /// <summary>The heading row of a sheet (also works for a header-only sheet, which MiniExcel.GetColumns can't read).</summary>
    private static List<string> Headings(byte[] bytes, string sheet) =>
        MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: false, sheetName: sheet).Cast<IDictionary<string, object>>().First().Values.Select(v => v?.ToString() ?? "").ToList();

    // ---- parser ----

    [Theory]
    [InlineData("import", CommandKind.ImportHelp, null)]
    [InlineData("Purana data", CommandKind.ImportHelp, null)]
    [InlineData("orders today", null, null)]
    [InlineData("import template", CommandKind.ImportTemplate, null)]
    [InlineData("import template customers", CommandKind.ImportTemplate, "customers")]
    [InlineData("import template catalog", CommandKind.ImportTemplate, "catalog")]
    [InlineData("import template all", CommandKind.ImportTemplate, "all")]
    [InlineData("import customers template", CommandKind.ImportTemplate, "customers")]
    public void Parser_RecognisesImportCommands(string text, CommandKind? kind, string? which)
    {
        var parsed = CommandParser.TryParse(text);
        if (kind is null) { Assert.True(parsed is null || parsed.Kind is not (CommandKind.ImportHelp or CommandKind.ImportTemplate)); return; }
        Assert.Equal(kind, parsed!.Kind);
        Assert.Equal(which, parsed.Text);
    }

    [Fact]
    public void Parser_SheetLinkIsStillTheCatalogSheetImport()
    {
        Assert.Equal(CommandKind.ImportCatalogSheet, CommandParser.TryParse("https://docs.google.com/spreadsheets/d/abc123/edit")!.Kind);
    }

    [Theory]
    [InlineData("3001234567", "03001234567")]
    [InlineData("+92 300 1234567", "03001234567")]
    [InlineData("0092-300-1234567", "03001234567")]
    [InlineData("923001234567", "03001234567")]
    [InlineData("0300-1234567", "03001234567")]
    [InlineData("abc", null)]
    [InlineData("123", null)]
    public void NormalizeImportPhone_FixesLostZeroAndCountryCode(string raw, string? expected) =>
        Assert.Equal(expected, ConversationEngine.NormalizeImportPhone(raw));

    // ---- guide tabs ----

    [Fact]
    public async Task Export_EveryColumnOfEverySheetIsDescribedOnTheGuideTab()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var seller = await db.Sellers.FirstAsync();
        db.Customers.Add(new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" });
        db.Discounts.Add(new OrderTrackerBot.Domain.Entities.Discount { SellerId = seller.Id, Code = "EID10", Type = DiscountType.Percent, Value = 10 });
        db.LoyaltyRules.Add(new OrderTrackerBot.Domain.Entities.LoyaltyRule { SellerId = seller.Id, OrderThreshold = 5, DiscountPercent = 10 });
        await db.SaveChangesAsync();
        var customer = await db.Customers.FirstAsync();
        var order = new OrderTrackerBot.Domain.Entities.Order { SellerId = seller.Id, CustomerId = customer.Id, Total = 100 };
        order.Items.Add(new OrderTrackerBot.Domain.Entities.OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 100, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "export all", default);

        var (_, bytes, _) = Assert.Single(_files);
        var sheetNames = MiniExcel.GetSheetNames(new MemoryStream(bytes)).ToList();
        Assert.Equal("Guide", sheetNames[^1]);
        var guide = MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: true, sheetName: "Guide").Cast<IDictionary<string, object>>().ToList();
        Assert.Contains(guide, r => (string)r["Sheet"] == "About" || (string)r["Sheet"] == "Move data");

        foreach (var sheet in sheetNames.Where(n => n != "Guide"))
        {
            var headers = Headings(bytes, sheet);
            Assert.NotEmpty(headers);
            foreach (var header in headers)
            {
                var row = guide.SingleOrDefault(r => (string)r["Sheet"] == sheet && (string?)r["Column"] == header);
                Assert.True(row is not null, $"Guide has no row for {sheet} / {header}");
                Assert.False(string.IsNullOrWhiteSpace((string?)row!["Needed?"]), $"Guide row for {sheet} / {header} has no 'Needed?'");
                Assert.False(string.IsNullOrWhiteSpace((string?)row["Kya likhna hai"]), $"Guide row for {sheet} / {header} has no help text");
            }
        }
    }

    [Fact]
    public async Task ImportTemplate_Customers_HasGuideFirst_AndEmptyCustomersSheetWithHeadings()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);

        await CreateEngine(db).HandleIncomingMessageAsync(Phone, "import template customers", default);

        var (name, bytes, caption) = Assert.Single(_files);
        Assert.Equal("Import-Customers-Template.xlsx", name);
        Assert.Contains("Guide", caption);
        Assert.Equal(new[] { "Guide", "Customers" }, MiniExcel.GetSheetNames(new MemoryStream(bytes)).ToArray());
        var headings = Headings(bytes, "Customers");
        Assert.Equal(new[] { "Name", "Phone", "City", "Address", "Preferred contact", "Notes" }, headings);
        Assert.Empty(MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: true, sheetName: "Customers"));
        var guide = MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: true, sheetName: "Guide").Cast<IDictionary<string, object>>().ToList();
        Assert.Contains(guide, r => (string)r["Sheet"] == "Step 1");
        Assert.Contains(guide, r => (string)r["Sheet"] == "Customers" && (string?)r["Column"] == "Name" && ((string)r["Needed?"]).StartsWith("REQUIRED"));
        Assert.DoesNotContain(guide, r => (string?)r["Column"] == "Total spent"); // calculated columns aren't in the template
    }

    [Fact]
    public async Task ImportTemplate_All_HasBothSheets_AndBareImportOffersTheThreeFiles()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleIncomingMessageAsync(Phone, "import", default);
        var (body, sections) = Assert.Single(_lists);
        Assert.True(body.Length <= 1024);
        var rows = sections.Single().Rows;
        Assert.Equal(new[] { "import template customers", "import template catalog", "import template all" }, rows.Select(r => r.Id).ToArray());
        Assert.All(rows, r => Assert.True(r.Title.Length <= 24));

        await engine.HandleIncomingMessageAsync(Phone, "import template all", default);
        var (_, bytes, _) = Assert.Single(_files);
        Assert.Equal(new[] { "Guide", "Customers", "Catalog" }, MiniExcel.GetSheetNames(new MemoryStream(bytes)).ToArray());
        Assert.Contains("Product", Headings(bytes, "Catalog"));
    }

    // ---- importing ----

    [Fact]
    public async Task Document_ChecksFirst_SavesOnlyAfterYes_SkipsExisting_AndUndoRemovesIt()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var seller = await db.Sellers.FirstAsync();
        db.Customers.Add(new OrderTrackerBot.Domain.Entities.Customer { SellerId = seller.Id, Name = "Old Sara", Phone = "0300-1112222" });
        await db.SaveChangesAsync();
        var engine = CreateEngine(db);

        ServeFile("m1", Workbook(
            new[]
            {
                Row("Ayesha Khan", "3001234567", "Lahore", "House 12"),   // lost leading zero
                Row("Bilal", "+92 321 7654321", "Karachi", null),
                Row("Sara", "03001112222", null, null),                   // same phone as the existing "Old Sara"
                Row("Ayesha Again", "0300-1234567", null, null),          // same phone twice in the file
                Row("", "03005550000", null, null),                       // no name
                Row("Zed", "12", null, null)                              // not a phone
            },
            new[]
            {
                Row("Kurti", "1800", "1200", "piece", "10", "Ali Traders", "fabric: cotton; season: summer"),
                Row("Lawn Suit", "3500", null, null, null, null, null),   // exists from onboarding
                Row("Sugar", "Rs. 1,250", null, "kg", null, null, null),
                Row("Broken", "abc", null, null, null, null, null),
                Row("Odd Unit", "10", null, "furlong", null, null, null)
            }));

        await engine.HandleDocumentMessageAsync(Phone, "m1", "old.xlsx", XlsxMime);

        Assert.Equal(1, await db.Customers.CountAsync()); // nothing saved yet
        var summary = Assert.Single(_buttonBodies);
        Assert.Contains("Customers: 2 naye", summary);
        Assert.Contains("1 pehle se maujood", summary);
        Assert.Contains("1 file mein dobara", summary);
        Assert.Contains("2 ghalat rows", summary);
        Assert.Contains("Products: 2 naye", summary);
        Assert.Contains("1 pehle se maujood", summary);
        Assert.Contains("Customers row 6: Name khali hai", summary);
        Assert.Contains("Catalog row 5: Price sahi nahi", summary);
        Assert.Contains("Catalog row 6: Unit sahi nahi", summary);
        Assert.Equal(ConversationState.AwaitingImportConfirmation, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "Yes", default);

        var customers = await db.Customers.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(new[] { "Old Sara", "Ayesha Khan", "Bilal" }, customers.Select(c => c.Name).ToArray());
        Assert.Equal("03001234567", customers[1].Phone);
        Assert.Equal("03217654321", customers[2].Phone);
        Assert.Equal("Lahore", customers[1].City);
        var kurti = await db.Products.SingleAsync(p => p.Name == "Kurti");
        Assert.Equal(1800m, kurti.Price);
        Assert.Equal(1200m, kurti.CostPrice);
        Assert.Equal(10, kurti.StockQty);
        Assert.Contains("Ali Traders", kurti.AttributesJson);
        Assert.Contains("cotton", kurti.AttributesJson);
        var sugar = await db.Products.SingleAsync(p => p.Name == "Sugar");
        Assert.Equal(1250m, sugar.Price);
        Assert.Equal("kg", sugar.UnitType);
        Assert.Equal(1, await db.Products.CountAsync(p => p.Name == "Lawn Suit"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_texts, t => t.Contains("Import ho gaya") && t.Contains("2 customers") && t.Contains("2 products"));

        await engine.HandleIncomingMessageAsync(Phone, "undo", default);

        Assert.Equal(new[] { "Old Sara" }, (await db.Customers.Select(c => c.Name).ToListAsync()).ToArray());
        Assert.Equal(new[] { "Lawn Suit" }, (await db.Products.Select(p => p.Name).ToListAsync()).ToArray());
        Assert.Contains(_texts, t => t.Contains("import hata diya"));
    }

    [Fact]
    public async Task Document_No_SavesNothing_AndResendingTheSameFileAfterImportReportsNothingNew()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var engine = CreateEngine(db);
        ServeFile("m1", Workbook(new[] { Row("Bilal", "03217654321", null, null) }, null));

        await engine.HandleDocumentMessageAsync(Phone, "m1", "c.xlsx", XlsxMime);
        await engine.HandleIncomingMessageAsync(Phone, "No", default);
        Assert.Equal(0, await db.Customers.CountAsync());
        Assert.Contains(_texts, t => t.Contains("kuch save nahi kiya"));

        await engine.HandleDocumentMessageAsync(Phone, "m1", "c.xlsx", XlsxMime);
        await engine.HandleIncomingMessageAsync(Phone, "Yes", default);
        Assert.Equal(1, await db.Customers.CountAsync());

        ClearOutput();
        await engine.HandleDocumentMessageAsync(Phone, "m1", "c.xlsx", XlsxMime);
        Assert.Empty(_buttonBodies);
        Assert.Contains(_texts, t => t.Contains("1 pehle se maujood") && t.Contains("koi nayi row nahi"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task Document_RejectsNonXlsx_UnreadableFiles_AndFilesWithoutKnownSheets()
    {
        using var db = _dbFactory.CreateContext();
        await OnboardAsync(db);
        var engine = CreateEngine(db);

        await engine.HandleDocumentMessageAsync(Phone, "m1", "old.csv", "text/csv");
        Assert.Contains(_texts, t => t.Contains("Sirf Excel file (.xlsx)"));

        ServeFile("junk", new byte[] { 1, 2, 3, 4 });
        await engine.HandleDocumentMessageAsync(Phone, "junk", "x.xlsx", XlsxMime);
        Assert.Contains(_texts, t => t.Contains("File khul nahi saki"));

        ServeFile("other", _writer.WriteXlsx(new[] { new ExportSheet("Sheet1", new[] { new ExportColumn("A", typeof(string)) }, new List<object?[]> { new object?[] { "x" } }) }));
        await engine.HandleDocumentMessageAsync(Phone, "other", "x.xlsx", XlsxMime);
        Assert.Contains(_texts, t => t.Contains("\"Customers\" ya \"Catalog\""));
        Assert.Equal(0, await db.Customers.CountAsync());
    }

    [Fact]
    public async Task Document_ExportFileFromAnotherAccount_ImportsItsCustomersAndCatalog()
    {
        // Seller A's real export (with its extra calculated columns and a Guide tab) is what seller B uploads.
        using var dbA = _dbFactory.CreateContext();
        await OnboardAsync(dbA);
        var a = await dbA.Sellers.FirstAsync();
        dbA.Customers.Add(new OrderTrackerBot.Domain.Entities.Customer { SellerId = a.Id, Name = "Sara", Phone = "03001112222", City = "Lahore" });
        await dbA.SaveChangesAsync();
        await CreateEngine(dbA).HandleIncomingMessageAsync(Phone, "export customers catalog", default);
        var export = Assert.Single(_files).Bytes;
        ClearOutput();
        await dbA.Sellers.ExecuteDeleteAsync();
        dbA.ChangeTracker.Clear();

        using var dbB = _dbFactory.CreateContext();
        await OnboardAsync(dbB);
        await dbB.Products.ExecuteDeleteAsync();
        ClearOutput();
        ServeFile("exp", export);
        var engine = CreateEngine(dbB);

        await engine.HandleDocumentMessageAsync(Phone, "exp", "Ayesha-export.xlsx", XlsxMime);
        await engine.HandleIncomingMessageAsync(Phone, "Yes", default);

        Assert.Equal("Sara", (await dbB.Customers.SingleAsync()).Name);
        Assert.Equal("Lawn Suit", (await dbB.Products.SingleAsync()).Name);
    }

    // ---- onboarding ----

    [Fact]
    public async Task Onboarding_PuranaData_OffersTemplates_ImportsFile_ThenContinuesWithBusinessName()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        await engine.HandleIncomingMessageAsync(Phone, "start", default);
        await engine.HandleIncomingMessageAsync(Phone, "Roman Urdu", default);
        Assert.Equal(ConversationState.OnboardingStartChoice, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "📥 Purana data", default);
        Assert.Equal(ConversationState.OnboardingStartChoice, (await db.Sessions.FirstAsync()).State);
        Assert.Single(_lists);

        await engine.HandleIncomingMessageAsync(Phone, "import template catalog", default);
        Assert.Equal("Import-Catalog-Template.xlsx", Assert.Single(_files).File);
        Assert.Equal(ConversationState.OnboardingStartChoice, (await db.Sessions.FirstAsync()).State);

        ServeFile("m1", Workbook(new[] { Row("Bilal", "03217654321", null, null) }, new[] { Row("Kurti", "1800", null, null, null, null, null) }));
        _buttonBodies.Clear();
        await engine.HandleDocumentMessageAsync(Phone, "m1", "old.xlsx", XlsxMime);
        Assert.Single(_buttonBodies);
        await engine.HandleIncomingMessageAsync(Phone, "Yes", default);

        Assert.Equal(1, await db.Customers.CountAsync());
        Assert.Equal(1, await db.Products.CountAsync());
        Assert.Equal(ConversationState.OnboardingBusinessName, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_buttonBodies, b => b.Contains("Business ka naam"));
    }

    [Fact]
    public async Task Document_DuringOtherOnboardingSteps_IsAskedToWait_AndSavesNothing()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein" })
            await engine.HandleIncomingMessageAsync(Phone, step, default); // now asked for the business name
        ServeFile("m1", Workbook(new[] { Row("Bilal", "03217654321", null, null) }, null));

        await engine.HandleDocumentMessageAsync(Phone, "m1", "old.xlsx", XlsxMime);

        Assert.Contains(_texts, t => t.Contains("Pehle setup ka sawal mukammal karein"));
        Assert.Equal(0, await db.Customers.CountAsync());
        Assert.Equal(ConversationState.OnboardingBusinessName, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public void Webhook_DocumentMessage_IsExtractedWithItsFileName_AndIsNoLongerUnsupported()
    {
        const string json = """
            {"entry":[{"changes":[{"value":{"messages":[
              {"id":"wamid.1","from":"923001234567","type":"document","document":{"id":"media-9","filename":"old.xlsx","mime_type":"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"}}
            ]}}]}]}
            """;
        var payload = System.Text.Json.JsonSerializer.Deserialize<OrderTrackerBot.Infrastructure.WhatsApp.WhatsAppWebhookPayload>(json)!;

        var doc = Assert.Single(payload.ExtractDocumentMessages());
        Assert.Equal(("923001234567", "media-9", "old.xlsx", XlsxMime, "wamid.1"), doc);
        Assert.Empty(payload.ExtractUnsupportedMessages());
    }

    [Fact]
    public async Task Onboarding_CatalogStep_AllowsAskingForTheImportFile()
    {
        using var db = _dbFactory.CreateContext();
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        ClearOutput();

        await engine.HandleIncomingMessageAsync(Phone, "import template catalog", default);

        Assert.Equal("Import-Catalog-Template.xlsx", Assert.Single(_files).File);
        Assert.Equal(ConversationState.OnboardingAddProduct, (await db.Sessions.FirstAsync()).State);
    }
}
