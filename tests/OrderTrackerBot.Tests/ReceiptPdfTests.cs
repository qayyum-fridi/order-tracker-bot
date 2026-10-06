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

    // 1x1 PNG
    internal static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void CanEmbedImage_AcceptsPng_RejectsJunkAndEmpty()
    {
        var generator = new ReceiptPdfGenerator();
        Assert.True(generator.CanEmbedImage(TinyPng));
        Assert.False(generator.CanEmbedImage(new byte[] { 1, 2, 3 }));
        Assert.False(generator.CanEmbedImage(Array.Empty<byte>()));
    }

    [Fact]
    public void Generate_WithLogoAndBanner_Works_AndBadImageFallsBackToPlainReceipt()
    {
        var generator = new ReceiptPdfGenerator();

        var branded = generator.Generate(Sample() with { Logo = TinyPng, Banner = TinyPng });
        var fallback = generator.Generate(Sample() with { Logo = new byte[] { 1, 2, 3 }, Banner = new byte[] { 9 } });

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(branded, 0, 5));
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(fallback, 0, 5));
    }
}
