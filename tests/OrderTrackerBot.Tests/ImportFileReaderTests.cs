using System.IO.Compression;
using System.Text;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Export;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Every spreadsheet format a seller might send is turned into the same plain cell grid; the format is told from the bytes, not the name.</summary>
public class ImportFileReaderTests
{
    private readonly ImportFileReader _reader = new();

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string[][] Cells(IReadOnlyList<ImportGridSheet>? sheets, int sheet = 0) =>
        Assert.IsAssignableFrom<IReadOnlyList<ImportGridSheet>>(sheets)[sheet].Rows.Select(r => r.Cells.ToArray()).ToArray();

    // ---- csv / tsv ----

    [Fact]
    public void Csv_QuotedCellsMayHoldCommasQuotesAndLineBreaks()
    {
        var sheets = _reader.Read(Utf8("Item,Price,Note\r\n\"Bread, white\",120,\"say \"\"hi\"\"\"\r\nButter,450.5,\"two\nlines\"\r\n"), "stock.csv");

        var sheet = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ImportGridSheet>>(sheets));
        Assert.Equal("stock", sheet.Name);
        Assert.Equal(new[] { "Item", "Price", "Note" }, sheet.Rows[0].Cells);
        Assert.Equal(new[] { "Bread, white", "120", "say \"hi\"" }, sheet.Rows[1].Cells);
        Assert.Equal(new[] { "Butter", "450.5", "two\nlines" }, sheet.Rows[2].Cells);
    }

    [Theory]
    [InlineData("Item;Price\nBread;120\n")]
    [InlineData("Item\tPrice\nBread\t120\n")]
    [InlineData("Item|Price\nBread|120\n")]
    [InlineData("sep=;\nItem;Price\nBread;120\n")]
    [InlineData("Item,Price\nBread,120")]
    public void Csv_TheDelimiterIsDetected(string text)
    {
        var rows = Cells(_reader.Read(Utf8(text), "x.csv"));

        Assert.Equal(new[] { new[] { "Item", "Price" }, new[] { "Bread", "120" } }, rows);
    }

    [Fact]
    public void Csv_AddressesWithCommasInASemicolonFileStayInTheirCell()
    {
        var rows = Cells(_reader.Read(Utf8("Name;Address\nAyesha;House 12, Street 4, Gulberg\nBilal;Shop 3, Mall Road\n"), "c.csv"));

        Assert.Equal("House 12, Street 4, Gulberg", rows[1][1]);
    }

    [Fact]
    public void Csv_BlankLinesAreSkipped_ButRowNumbersStayTheLineNumbers()
    {
        var sheet = Assert.Single(_reader.Read(Utf8("A,B\n\nx,y\n"), null)!);

        Assert.Equal(new[] { 1, 3 }, sheet.Rows.Select(r => r.Number).ToArray());
        Assert.Equal("Sheet1", sheet.Name);
    }

    [Fact]
    public void Csv_Utf8WithBom_KeepsUrduAndNoBomInTheFirstHeading()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Utf8("نام,فون\nعلی,03001234567\n")).ToArray();

        var rows = Cells(_reader.Read(bytes, "گاہک.csv"));

        Assert.Equal(new[] { "نام", "فون" }, rows[0]);
        Assert.Equal(new[] { "علی", "03001234567" }, rows[1]);
    }

    [Fact]
    public void Csv_Utf16TabSeparated_ExcelUnicodeText_IsRead()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Item\tPrice\nBread\t120\n")).ToArray();

        Assert.Equal(new[] { new[] { "Item", "Price" }, new[] { "Bread", "120" } }, Cells(_reader.Read(bytes, "u.txt")));
    }

    [Fact]
    public void Csv_NotUtf8_FallsBackToLatin1()
    {
        var bytes = new byte[] { (byte)'N', (byte)'a', (byte)'m', (byte)'e', (byte)'\n', (byte)'C', (byte)'a', (byte)'f', 0xE9 };

        Assert.Equal("Café", Cells(_reader.Read(bytes, "l.csv"))[1][0]);
    }

    [Fact]
    public void CellsAreTrimmed_AndLeadingZerosOfTextArePreserved()
    {
        var rows = Cells(_reader.Read(Utf8("Name , Phone\n Ayesha ,03001234567\n"), "c.csv"));

        Assert.Equal(new[] { "Name", "Phone" }, rows[0]);
        Assert.Equal(new[] { "Ayesha", "03001234567" }, rows[1]);
    }

    // ---- things that are not spreadsheets ----

    [Fact]
    public void BinaryFilesAreRefused()
    {
        Assert.Null(_reader.Read(new byte[] { 1, 2, 3, 4 }, "x.xlsx"));
        Assert.Null(_reader.Read(Utf8("%PDF-1.4\n%\u0000\u0001 binary"), "x.csv"));
    }

    [Fact]
    public void AZipThatIsNotASpreadsheet_Docx_IsRefused()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open())) writer.Write("<w:document/>");

        Assert.Null(_reader.Read(stream.ToArray(), "letter.docx"));
    }

    // ---- ods ----

    private static byte[] Ods(string tables)
    {
        var content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">" +
            "<office:body><office:spreadsheet>" + tables + "</office:spreadsheet></office:body></office:document-content>";
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var mime = new StreamWriter(zip.CreateEntry("mimetype").Open())) mime.Write("application/vnd.oasis.opendocument.spreadsheet");
            using (var xml = new StreamWriter(zip.CreateEntry("content.xml").Open(), new UTF8Encoding(false))) xml.Write(content);
        }
        return stream.ToArray();
    }

    [Fact]
    public void Ods_ReadsEverySheet_NumbersTextAndDates_WithoutExpandingRepeatedEmptyCells()
    {
        var bytes = Ods(
            "<table:table table:name=\"Products\">" +
            "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>Item</text:p></table:table-cell><table:table-cell office:value-type=\"string\"><text:p>Rate</text:p></table:table-cell><table:table-cell table:number-columns-repeated=\"16000\"/></table:table-row>" +
            "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>Lawn<text:s text:c=\"2\"/>Suit</text:p></table:table-cell><table:table-cell office:value-type=\"float\" office:value=\"3500\"><text:p>3,500</text:p></table:table-cell></table:table-row>" +
            "<table:table-row table:number-rows-repeated=\"1048000\"><table:table-cell table:number-columns-repeated=\"16384\"/></table:table-row>" +
            "</table:table>" +
            "<table:table table:name=\"Clients\">" +
            "<table:table-row><table:table-cell office:value-type=\"string\" office:string-value=\"Name\"/></table:table-row>" +
            "<table:table-row table:number-rows-repeated=\"3\"><table:table-cell table:number-columns-repeated=\"2\"/></table:table-row>" +
            "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>Ayesha</text:p></table:table-cell><table:table-cell office:value-type=\"date\" office:date-value=\"2026-09-30\"/></table:table-row>" +
            "</table:table>");

        var sheets = Assert.IsAssignableFrom<IReadOnlyList<ImportGridSheet>>(_reader.Read(bytes, "stock.ods"));

        Assert.Equal(new[] { "Products", "Clients" }, sheets.Select(s => s.Name).ToArray());
        Assert.Equal(new[] { new[] { "Item", "Rate" }, new[] { "Lawn  Suit", "3500" } }, Cells(sheets, 0));
        Assert.Equal(new[] { 1, 5 }, sheets[1].Rows.Select(r => r.Number).ToArray());
        Assert.Equal(new[] { "Ayesha", "2026-09-30" }, sheets[1].Rows[1].Cells);
    }

    [Fact]
    public void Ods_WithADoctype_IsRefused()
    {
        var evil = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"/>";
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            using (var xml = new StreamWriter(zip.CreateEntry("content.xml").Open())) xml.Write(evil);

        Assert.Null(_reader.Read(stream.ToArray(), "x.ods"));
    }

    // ---- xls (Excel 97-2003) ----

    [Fact]
    public void Xls_ReadsEverySheet_NumbersAsPlainText_AndKeepsTextBarcodesWithTheirLeadingZero()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "own-layout.xls"));

        var sheets = Assert.IsAssignableFrom<IReadOnlyList<ImportGridSheet>>(_reader.Read(bytes, "own-layout.xls"));

        Assert.Equal(new[] { "Stock", "Clients" }, sheets.Select(s => s.Name).ToArray());
        Assert.Equal(new[] { "Item", "Rate", "Qty", "Barcode" }, sheets[0].Rows[0].Cells);
        Assert.Equal(new[] { "Bread", "120", "10", "0123456789012" }, sheets[0].Rows[1].Cells);
        Assert.Equal(new[] { "Butter", "450.5", "4", "0123456789013" }, sheets[0].Rows[2].Cells);
        Assert.Equal(new[] { "Ayesha", "3001234567" }, sheets[1].Rows[1].Cells);
    }

    // ---- which uploads are accepted at all ----

    [Theory]
    [InlineData("stock.xlsx", null, true)]
    [InlineData("STOCK.XLS", null, true)]
    [InlineData("list.ods", null, true)]
    [InlineData("list.csv", "application/octet-stream", true)]
    [InlineData("list.tsv", null, true)]
    [InlineData("list.txt", null, true)]
    [InlineData("file", "text/csv", true)]
    [InlineData(null, "application/vnd.ms-excel", true)]
    [InlineData(null, "text/csv; charset=utf-8", true)]
    [InlineData("letter.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", false)]
    [InlineData("scan.pdf", "application/pdf", false)]
    [InlineData(null, null, false)]
    public void ImportFileTypes_AcceptsSpreadsheetsAndDelimitedText(string? name, string? mime, bool expected) =>
        Assert.Equal(expected, ImportFileTypes.IsSupported(name, mime));
}
