using System.Text;
using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Export;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Daily sales import: the file planner (pure) and the dashboard preview, commit and undo through the engine.</summary>
public class SalesImportPlannerTests
{
    private static readonly DateOnly Today = new(2026, 10, 19);

    private static SalesImportParse ParseCsv(string csv) =>
        SalesImportPlanner.Parse(new ImportFileReader().Read(Encoding.UTF8.GetBytes(csv), "sales.csv")!, Today);

    [Fact]
    public void Rows_of_one_customer_and_day_form_one_order()
    {
        var parse = ParseCsv("""
            Date,Customer,Phone,Product,Quantity,Price,Payment,Status
            2026-10-18,Ayesha Khan,0300-1234567,Cotton Kurti,2,950,COD,Delivered
            2026-10-18,Ayesha Khan,0300-1234567,Lawn Suit,1,2500,COD,Delivered
            2026-10-17,Ayesha Khan,0300-1234567,Cotton Kurti,1,950,COD,Delivered
            """);

        Assert.Null(parse.Problem);
        Assert.Equal(2, parse.Orders.Count);
        Assert.Equal(2, parse.Orders[0].Lines.Count);
        Assert.Equal("03001234567", parse.Orders[0].Phone);
    }

    [Fact]
    public void Headings_are_found_loosely_and_unknown_columns_are_listed_not_guessed()
    {
        var parse = ParseCsv("""
            Sale Date,Buyer Name,Item Name,Qty,Unit Price,Total,Notes
            2026-10-18,Sara,Cotton Kurti,1,950,950,fast
            """);

        Assert.Null(parse.Problem);
        Assert.Contains(parse.MappedColumns, c => c.Field == "Product" && c.Source == "Item Name");
        Assert.Contains(parse.MappedColumns, c => c.Field == "Price" && c.Source == "Unit Price");
        Assert.Contains("Total", parse.IgnoredHeadings);
        Assert.Contains("Notes", parse.IgnoredHeadings);
    }

    [Fact]
    public void A_file_without_product_and_quantity_is_not_a_sales_file()
    {
        var parse = ParseCsv("Name,Phone,City\nSara,0300,Lahore\n");
        Assert.Equal("NoSales", parse.Problem);
    }

    [Theory]
    [InlineData("Date,Customer,Product,Quantity\n2026-10-18,Sara,Kurti,0\n", "quantity")]
    [InlineData("Date,Customer,Product,Quantity\n2026-13-45,Sara,Kurti,1\n", "date")]
    [InlineData("Date,Customer,Product,Quantity\n2026-12-01,Sara,Kurti,1\n", "future")]
    [InlineData("Date,Customer,Product,Quantity,Payment\n2026-10-18,Sara,Kurti,1,Crypto\n", "payment")]
    [InlineData("Date,Product,Quantity,Phone\n2026-10-18,Kurti,1,12345\n", "phone")]
    public void Bad_rows_are_reported_with_their_row_number(string csv, string expected)
    {
        var parse = ParseCsv(csv);
        var error = Assert.Single(parse.Errors);
        Assert.Equal(2, error.Row);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_second_row_with_a_different_status_is_refused_not_merged()
    {
        var parse = ParseCsv("""
            Date,Customer,Product,Quantity,Status
            2026-10-18,Sara,Kurti,1,Delivered
            2026-10-18,Sara,Lawn,1,Pending
            """);

        Assert.Single(parse.Orders);
        Assert.Equal(1, parse.Orders[0].Lines.Count);
        Assert.Contains(parse.Errors, e => e.Row == 3 && e.Message.Contains("differs"));
    }

    [Fact]
    public void Excel_serial_dates_are_read_as_days()
    {
        var parse = ParseCsv("Date,Customer,Product,Quantity\n46127,Sara,Kurti,1\n");
        Assert.Null(parse.Problem);
        Assert.Equal(new DateOnly(2026, 4, 15), parse.Orders[0].Day);
    }
}

public class SalesImportEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _texts = new();

    public SalesImportEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _texts.Add(t)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    public void Dispose() { }

    private ConversationEngine Engine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), importReader: new ImportFileReader());

    private async Task<AppDbContext> ShopAsync()
    {
        var db = _dbFactory.CreateContext();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Cotton Kurti", Price = 950, StockQty = 10 });
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Lawn Suit", Price = 2500 });
        await db.SaveChangesAsync();
        return db;
    }

    private static byte[] Csv(string text) => Encoding.UTF8.GetBytes(text);

    private static string Day(int daysAgo) => DateTime.UtcNow.AddHours(5).AddDays(-daysAgo).ToString("yyyy-MM-dd");

    private string Sales() => $"""
        Date,Customer,Phone,Product,Quantity,Price,Payment,Status
        {Day(1)},Ayesha Khan,0300-1234567,Cotton Kurti,2,,COD,Delivered
        {Day(1)},Ayesha Khan,0300-1234567,Lawn Suit,1,,COD,Delivered
        {Day(1)},Sara,,Cotton Kurti,1,900,Bank,Pending
        {Day(1)},Sara,,Mystery Item,1,100,Bank,Pending
        """;

    [Fact]
    public async Task Preview_counts_orders_and_reports_the_unknown_product_row()
    {
        using var db = await ShopAsync();
        var preview = await Engine(db).PreviewClientImportAsync((await db.Sellers.SingleAsync()).Id, "sales", Csv(Sales()), "sales.csv", default);

        Assert.Null(preview.Problem);
        Assert.Equal(2, preview.NewCount);
        var error = Assert.Single(preview.Errors);
        Assert.Contains("row 5", error);
        Assert.Contains("Mystery Item", error);
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Commit_saves_orders_with_catalog_prices_stock_and_payment_rules()
    {
        using var db = await ShopAsync();
        var sellerId = (await db.Sellers.SingleAsync()).Id;

        var result = await Engine(db).CommitClientImportAsync(sellerId, "sales", Csv(Sales()), "sales.csv", default);

        Assert.NotNull(result);
        Assert.Equal(2, result.Created);
        var ayesha = await db.Orders.Include(o => o.Items).Include(o => o.Customer)
            .SingleAsync(o => o.Customer!.Name == "Ayesha Khan");
        Assert.Equal(4400, ayesha.Total);                        // 2 × 950 + 1 × 2500, prices from the catalog
        Assert.Equal(OrderStatus.Delivered, ayesha.Status);
        Assert.Equal(PaymentStatus.Paid, ayesha.PaymentStatus);  // cash on delivery, delivered
        Assert.Equal("03001234567", ayesha.Customer!.Phone);
        var sara = await db.Orders.Include(o => o.Customer).SingleAsync(o => o.Customer!.Name == "Sara");
        Assert.Equal(900, sara.Total);                           // the row's own price
        Assert.Equal(PaymentStatus.Paid, sara.PaymentStatus);    // bank sales count as paid
        Assert.Equal(7, (await db.Products.SingleAsync(p => p.Name == "Cotton Kurti")).StockQty); // 10 − 2 − 1
    }

    [Fact]
    public async Task Undo_cancels_the_imported_orders_and_gives_the_stock_back()
    {
        using var db = await ShopAsync();
        var sellerId = (await db.Sellers.SingleAsync()).Id;
        await Engine(db).CommitClientImportAsync(sellerId, "sales", Csv(Sales()), "sales.csv", default);

        await Engine(db).HandleIncomingMessageAsync(Phone, "undo", default);

        Assert.All(await db.Orders.ToListAsync(), o => Assert.Equal(OrderStatus.Cancelled, o.Status));
        Assert.Equal(10, (await db.Products.SingleAsync(p => p.Name == "Cotton Kurti")).StockQty);
    }

    [Fact]
    public async Task Importing_the_same_file_twice_is_not_special_cased_but_each_import_is_its_own_undo_step()
    {
        using var db = await ShopAsync();
        var sellerId = (await db.Sellers.SingleAsync()).Id;
        await Engine(db).CommitClientImportAsync(sellerId, "sales", Csv(Sales()), "sales.csv", default);
        var second = await Engine(db).CommitClientImportAsync(sellerId, "sales", Csv(Sales()), "sales.csv", default);

        Assert.NotNull(second);
        Assert.Equal(4, await db.Orders.CountAsync());
    }
}
