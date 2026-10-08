using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Pdf;
using Xunit;

namespace OrderTrackerBot.Tests;

public class TaxTests
{
    [Theory]
    [InlineData("ntn 1234567-8", "ntn", "1234567-8")]
    [InlineData("NTN: 1234567-8", "ntn", "1234567-8")]
    [InlineData("strn 17-00-8888-001-37", "strn", "17-00-8888-001-37")]
    [InlineData("ntn", "ntn", null)]
    [InlineData("strn off", "strn", "off")]
    [InlineData("ntn hata do", "ntn", "hata do")]
    public void ParsesNtnAndStrn(string message, string kind, string? value)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.TaxSettings, parsed!.Kind);
        Assert.Equal(kind, parsed.Text);
        Assert.Equal(value, parsed.Text2);
    }

    [Theory]
    [InlineData("sales tax 18", "set", 18.0)]
    [InlineData("Sales Tax 18%", "set", 18.0)]
    [InlineData("gst 17.5", "set", 17.5)]
    [InlineData("sales tax: 5", "set", 5.0)]
    [InlineData("sales tax off", "off", null)]
    [InlineData("sales tax", null, null)]
    public void ParsesSalesTaxRate(string message, string? mode, double? rate)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.TaxSettings, parsed!.Kind);
        Assert.Equal("rate", parsed.Text);
        Assert.Equal(mode, parsed.Text2);
        Assert.Equal(rate is null ? null : (decimal)rate, parsed.Amount);
    }

    [Fact]
    public void TaxAlone_ShowsTheSetup()
    {
        var parsed = CommandParser.TryParse("tax");
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.TaxSettings, parsed!.Kind);
        Assert.Equal("show", parsed.Text);
    }

    [Theory]
    [InlineData("order 12 withheld 120", 12, 120)]
    [InlineData("12 wht 120", 12, 120)]
    [InlineData("order 12 tax withheld 0", 12, 0)]
    [InlineData("#7 withholding Rs 85.5", 7, 85.5)]
    public void ParsesTaxWithheld(string message, int order, double amount)
    {
        var parsed = CommandParser.TryParse(message);
        Assert.NotNull(parsed);
        Assert.Equal(CommandKind.OrderTaxWithheld, parsed!.Kind);
        Assert.Equal(order, parsed.Number);
        Assert.Equal((decimal)amount, parsed.Amount);
    }

    [Theory]
    [InlineData(1180, 18, 180)]
    [InlineData(1000, 18, 152.54)]
    [InlineData(1000, 0, 0)]
    [InlineData(0, 18, 0)]
    public void SalesTax_IsExtractedFromTheInclusiveTotal(double total, double rate, double expected) =>
        Assert.Equal((decimal)expected, SalesTax.Amount((decimal)total, (decimal)rate));

    [Fact]
    public void NetOfWithheld_NeverGoesNegative() =>
        Assert.Equal(0, SalesTax.NetOfWithheld(new Order { Total = 100, TaxWithheld = 150 }));

    [Fact]
    public void TaxInvoicePdf_RendersWithTaxIdsAndNumber()
    {
        var data = ReceiptPdfTests.Sample(paid: true) with
        {
            ReceiptNumber = 5, Ntn = "1234567-8", Strn = "17-00-8888-001-37", SalesTaxRate = 18, SalesTaxAmount = 1208.14m
        };

        var pdf = new ReceiptPdfGenerator().Generate(data);

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }
}
