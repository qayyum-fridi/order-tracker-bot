using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Conversation;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>How a seller's own headings are read (no file, no database): which sheet is a customer / product list and which column is which field.</summary>
public class ImportColumnMapperTests
{
    private static ImportGridSheet Sheet(string name, params string[][] rows) =>
        new(name, rows.Select((r, i) => new ImportGridRow(i + 1, r)).ToList());

    private static string[] Row(string headings) => headings.Split('|');

    private static string Uses(MappedImportSheet sheet) => string.Join(", ", sheet.Understood.Select(u => $"{u.Field}={u.Source}"));

    [Theory]
    [InlineData("Item Name|Rate|Qty", "Product=Item Name, Price=Rate, Stock=Qty")]
    [InlineData("Purchase Price|Sale Price|Item", "Product=Item, Price=Sale Price, Cost price=Purchase Price")]
    [InlineData("Product name|Barcode|Catagory|Cost price|Price|Variation Colour|Variation size|Stock quantity",
        "Product=Product name, Price=Price, Cost price=Cost price, Category=Catagory, Size=Variation size, Color=Variation Colour, SKU=Barcode, Stock=Stock quantity")]
    [InlineData("Maal|Qeemat|Mojood", "Product=Maal, Price=Qeemat, Stock=Mojood")]
    [InlineData("نام|قیمت|اسٹاک", "Product=نام, Price=قیمت, Stock=اسٹاک")]
    [InlineData("Name|Price|Supplier Phone", "Product=Name, Price=Price")]
    [InlineData("Customer|Phone|Product|Price", "Product=Product, Price=Price")]
    [InlineData("Title|MRP|Unit|Pack Size|Brand|Supplier|Dept", "Product=Title, Price=MRP, Unit=Unit, Pack size=Pack Size, Vendor=Supplier, Manufacturer=Brand, Department=Dept")]
    public void ProductList_IsRecognisedFromTheHeadings(string headings, string expected)
    {
        var mapped = Assert.Single(ImportColumnMapper.Map(new[] { Sheet("Sheet1", Row(headings), Row(string.Join("|", Row(headings).Select(_ => "x")))) }).Sheets);

        Assert.Equal(ImportKind.Catalog, mapped.Kind);
        Assert.Equal(expected, Uses(mapped));
    }

    [Theory]
    [InlineData("Customer Name|Mobile No|Shehar|Pata", "Name=Customer Name, Phone=Mobile No, City=Shehar, Address=Pata")]
    [InlineData("نام|فون نمبر|شہر|پتہ", "Name=نام, Phone=فون نمبر, City=شہر, Address=پتہ")]
    [InlineData("Naam|WhatsApp|Preferred contact|Notes", "Name=Naam, Phone=WhatsApp, Preferred contact=Preferred contact, Notes=Notes")]
    [InlineData("Client|Cell|Location", "Name=Client, Phone=Cell, Address=Location")]
    public void CustomerList_IsRecognisedFromTheHeadings(string headings, string expected)
    {
        var mapped = Assert.Single(ImportColumnMapper.Map(new[] { Sheet("Sheet1", Row(headings), Row(string.Join("|", Row(headings).Select(_ => "x")))) }).Sheets);

        Assert.Equal(ImportKind.Customers, mapped.Kind);
        Assert.Equal(expected, Uses(mapped));
    }

    [Fact]
    public void Headings_OutputUsesOurOwnNames_SoTheNormalImportChecksRunUnchanged()
    {
        var mapped = Assert.Single(ImportColumnMapper.Map(new[]
        {
            Sheet("Stock", Row("Item|Rate|Qty|Barcode"), Row("Bread|120|10|0123456789012"))
        }).Sheets);

        var row = Assert.Single(mapped.Sheet.Rows);
        Assert.Equal(2, row.Number);
        Assert.Equal("Bread", row.Cells["Product"]);
        Assert.Equal("120", row.Cells["Price"]);
        Assert.Equal("10", row.Cells["Stock"]);
        Assert.Equal("0123456789012", row.Cells["SKU"]);
    }

    [Fact]
    public void TitleRowsAboveTheHeadings_AreSkipped_AndRowNumbersStayTheOnesInTheFile()
    {
        var mapped = Assert.Single(ImportColumnMapper.Map(new[]
        {
            Sheet("Products", new[] { "Bread & Butter Products List" }, Row("Item|Price"), Row("Bread|120"), Row("Butter|450"))
        }).Sheets);

        Assert.Equal(new[] { 3, 4 }, mapped.Sheet.Rows.Select(r => r.Number).ToArray());
        Assert.Equal("Product=Item, Price=Price", Uses(mapped));
    }

    [Fact]
    public void SheetsWithNothingUsable_AreReported_ButOurOwnGuideAndExportOnlyTabsAreSilentlySkipped()
    {
        var mapping = ImportColumnMapper.Map(new[]
        {
            Sheet("Guide", Row("Sheet|Column|Needed?"), Row("Customers|Name|REQUIRED")),
            Sheet("Orders", Row("Customer|Phone|Items|Total"), Row("Sara|0300|1 x Kurti|500")),
            Sheet("Needs Checking", Row("Excel row|Photo|What to check"), Row("7|Photo 1|Barcode faint")),
            Sheet("Products", Row("Item|Price"), Row("Bread|120"))
        });

        Assert.Equal("Products", Assert.Single(mapping.Sheets).Name);
        Assert.Equal(new[] { "Needs Checking" }, mapping.SkippedSheets.ToArray());
    }

    [Fact]
    public void WhenTwoColumnsMeanTheSameField_TheFirstWins_AndTheOtherIsListedAsLeftOut()
    {
        var mapped = Assert.Single(ImportColumnMapper.Map(new[]
        {
            Sheet("Sheet1", Row("Item|Price|Sale price|Fabric"), Row("Bread|120|130|cotton"))
        }).Sheets);

        Assert.Equal("Product=Item, Price=Price", Uses(mapped));
        Assert.Equal(new[] { "Sale price", "Fabric" }, mapped.Ignored.ToArray());
    }

    [Fact]
    public void OurOwnCalculatedColumns_AreNotReportedAsLeftOut()
    {
        var mapped = Assert.Single(ImportColumnMapper.Map(new[]
        {
            Sheet("Customers", Row("Name|Phone|City|Address|Preferred contact|Notes|Orders|Total spent|Last order|Customer since"),
                Row("Sara|03001112222|Lahore|House 1||| 3|12000|2026-09-30|2026-01-15"))
        }).Sheets);

        Assert.Empty(mapped.Ignored);
    }

    [Fact]
    public void PhoneColumnWithAnUnknownHeading_IsGuessedFromTheValues_AndSaidSo()
    {
        var mapped = Assert.Single(ImportColumnMapper.Map(new[]
        {
            Sheet("Sheet1", Row("Name|Tel|City"), Row("Ayesha|3001234567|Lahore"), Row("Bilal|+92 321 7654321|Karachi"), Row("Sara|0300-1112222|"))
        }).Sheets);

        Assert.Equal(ImportKind.Customers, mapped.Kind);
        var phone = Assert.Single(mapped.Understood, u => u.Field == "Phone");
        Assert.Equal("Tel", phone.Source);
        Assert.True(phone.Guessed);
    }

    [Fact]
    public void AColumnOfWordsIsNeverGuessedAsAPhone()
    {
        var mapping = ImportColumnMapper.Map(new[]
        {
            Sheet("Sheet1", Row("Name|Tel"), Row("Ayesha|call later"), Row("Bilal|shop closed"))
        });

        Assert.Empty(mapping.Sheets);
        Assert.Equal(new[] { "Sheet1" }, mapping.SkippedSheets.ToArray());
    }

    [Fact]
    public void ABarcodeColumn_IsABarcode_NotAPhoneNumber()
    {
        var mapped = Assert.Single(ImportColumnMapper.Map(new[]
        {
            Sheet("Sheet1", Row("Product name|Barcode|Price"), Row("Tea|8961103600547|30"), Row("Milk|8961103600554|50"))
        }).Sheets);

        Assert.Equal("Product=Product name, Price=Price, SKU=Barcode", Uses(mapped));
        Assert.DoesNotContain(mapped.Understood, u => u.Field == "Phone");
    }

    [Fact]
    public void SheetNamedCustomers_OnlyNeedsANameColumn()
    {
        var mapped = Assert.Single(ImportColumnMapper.Map(new[] { Sheet("Customers", Row("Name|Remarks"), Row("Ayesha|VIP")) }).Sheets);

        Assert.Equal(ImportKind.Customers, mapped.Kind);
    }

    [Fact]
    public void NoHeadingsAtAll_IsNotGuessedAtFromTheData()
    {
        var mapping = ImportColumnMapper.Map(new[] { Sheet("Sheet1", Row("Ayesha|03001234567|Lahore"), Row("Bilal|03217654321|Karachi")) });

        Assert.Empty(mapping.Sheets);
    }

    [Fact]
    public void LongDataLikeCells_AreNotMistakenForHeadings()
    {
        // The first row is data whose words ("Stock", "Price") sit inside long sentences: it must not win over the real heading row below it.
        var mapped = Assert.Single(ImportColumnMapper.Map(new[]
        {
            Sheet("Sheet1", new[] { "Please update the stock and price list before Friday", "thanks" }, Row("Item|Price"), Row("Bread|120"))
        }).Sheets);

        Assert.Equal("Product=Item, Price=Price", Uses(mapped));
        Assert.Equal(3, Assert.Single(mapped.Sheet.Rows).Number);
    }
}
