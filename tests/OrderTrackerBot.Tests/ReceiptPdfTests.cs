using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Infrastructure.Pdf;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ReceiptPdfTests
{
    internal static ReceiptData Sample(bool paid = false, string customer = "Ayesha Khan") => new(
        12, new DateTime(2026, 10, 6, 14, 30, 0), "Ali Collections", "923001234567", "Lahore", "@ali.collections",
        customer, "03001112222", "House 5, Street 3, Gulberg, Lahore",
        new[] { new ReceiptLine("Lawn Suit", 2, 3500, 7000), new ReceiptLine("Kurti", 1, 1800, 1800) },
        8800, 880, "EID10", 7920, "Bank / wallet transfer", paid, "PENDING", null, null,
        paid ? Array.Empty<string>() : new[] { "JazzCash: 03001234567", "Bank: HBL 1234-5678" });

    [Fact]
    public void Generate_ProducesPdfBytes()
    {
        var pdf = new ReceiptPdfGenerator().Generate(Sample());

        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Fact]
    public void Generate_HandlesUrduScriptAndEmptyOptionalFields()
    {
        var data = Sample(paid: true, customer: "عائشہ خان") with { CustomerPhone = null, CustomerAddress = "", BusinessCity = null, InstagramHandle = null, DiscountAmount = 0, DiscountCode = null };

        var pdf = new ReceiptPdfGenerator().Generate(data);

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }
}
