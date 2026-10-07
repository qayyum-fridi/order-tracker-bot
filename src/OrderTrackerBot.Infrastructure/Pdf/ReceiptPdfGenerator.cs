using System.Globalization;
using OrderTrackerBot.Application.Abstractions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OrderTrackerBot.Infrastructure.Pdf;

/// <summary>
/// A5 order receipt. QuestPDF Community licence (free below US$1M annual revenue — switch the licence if that stops being true).
/// Fonts: bundled Lato for Latin; Urdu-script names need an Arabic-script font installed on the host (see Dockerfile).
/// </summary>
public sealed class ReceiptPdfGenerator : IReceiptPdfGenerator
{
    private static readonly string Ink = Colors.Grey.Darken4;
    private static readonly string Muted = Colors.Grey.Darken1;
    private static readonly string Line = Colors.Grey.Lighten2;
    private static readonly string Accent = "#0B6E4F";

    static ReceiptPdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // A glyph no font covers must render as a box, never throw and lose the receipt.
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;

        var assembly = typeof(ReceiptPdfGenerator).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            QuestPDF.Drawing.FontManager.RegisterFont(stream);
        }
    }

    // Lato (bundled with QuestPDF) for Latin, embedded Noto Naskh Arabic for Urdu-script names and addresses.
    private static TextStyle Base => TextStyle.Default.FontSize(10).FontColor(Ink).FontFamily("Lato")
        .Fallback(a => a.FontFamily("Noto Naskh Arabic"));

    private static string Money(decimal amount) => "Rs. " + amount.ToString("#,0.##", CultureInfo.InvariantCulture);

    public bool CanEmbedImage(byte[] image)
    {
        try
        {
            return image.Length is > 0 and <= 5_000_000 && QuestPDF.Infrastructure.Image.FromBinaryData(image) is not null;
        }
        catch
        {
            return false;
        }
    }

    public byte[] Generate(ReceiptData r)
    {
        try
        {
            return Render(r);
        }
        catch when (r.Logo is not null || r.Banner is not null)
        {
            // A stored image the PDF engine can't draw must never cost the seller their receipt.
            return Render(r with { Logo = null, Banner = null });
        }
    }

    private static byte[] Render(ReceiptData r)
    {
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(28);
            page.DefaultTextStyle(Base);

            page.Header().Column(col =>
            {
                if (r.Banner is not null)
                    col.Item().PaddingBottom(8).AlignCenter().MaxHeight(90).Image(r.Banner).FitArea();
                col.Item().Row(row =>
                {
                    if (r.Logo is not null)
                        row.ConstantItem(56).PaddingRight(8).Height(48).Image(r.Logo).FitArea();
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(r.BusinessName).FontSize(16).Bold().FontColor(Accent);
                        var sub = string.Join(" · ", new[] { r.BusinessCity, r.BusinessPhone is null ? null : "WhatsApp " + r.BusinessPhone, r.InstagramHandle }
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                        if (sub.Length > 0) c.Item().Text(sub).FontSize(9).FontColor(Muted);
                    });
                    row.ConstantItem(120).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text("RECEIPT").FontSize(14).Bold();
                        c.Item().AlignRight().Text($"#{r.OrderId}").FontSize(11);
                        c.Item().AlignRight().Text(r.OrderedAtLocal.ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture)).FontSize(9).FontColor(Muted);
                    });
                });
                col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Line);
            });

            page.Content().PaddingVertical(10).Column(col =>
            {
                col.Spacing(10);

                col.Item().Column(c =>
                {
                    c.Item().Text("BILL TO").FontSize(8).Bold().FontColor(Muted);
                    c.Item().Text(r.CustomerName).Bold();
                    if (!string.IsNullOrWhiteSpace(r.CustomerPhone)) c.Item().Text(r.CustomerPhone);
                    if (!string.IsNullOrWhiteSpace(r.CustomerAddress)) c.Item().Text(r.CustomerAddress);
                });

                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(5);
                        c.ConstantColumn(30);
                        c.RelativeColumn(2);
                        c.RelativeColumn(2);
                    });
                    table.Header(h =>
                    {
                        static IContainer Head(IContainer c) => c.BorderBottom(1).BorderColor(Line).PaddingBottom(4);
                        h.Cell().Element(Head).Text("Item").FontSize(8).Bold().FontColor(Muted);
                        h.Cell().Element(Head).AlignRight().Text("Qty").FontSize(8).Bold().FontColor(Muted);
                        h.Cell().Element(Head).AlignRight().Text("Price").FontSize(8).Bold().FontColor(Muted);
                        h.Cell().Element(Head).AlignRight().Text("Amount").FontSize(8).Bold().FontColor(Muted);
                    });
                    foreach (var item in r.Lines)
                    {
                        static IContainer Body(IContainer c) => c.BorderBottom(0.5f).BorderColor(Line).PaddingVertical(4);
                        table.Cell().Element(Body).Column(nameCol =>
                        {
                            nameCol.Item().Text(item.Name);
                            if (!string.IsNullOrWhiteSpace(item.Details)) nameCol.Item().Text(item.Details).FontSize(8).FontColor(Muted);
                        });
                        table.Cell().Element(Body).AlignRight().Text(item.Quantity.ToString(CultureInfo.InvariantCulture));
                        table.Cell().Element(Body).AlignRight().Text(Money(item.UnitPrice));
                        table.Cell().Element(Body).AlignRight().Text(Money(item.LineTotal));
                    }
                });

                col.Item().AlignRight().Width(190).Column(c =>
                {
                    void Row(string label, string value, bool bold = false, string? color = null) => c.Item().Row(row =>
                    {
                        row.RelativeItem().Text(label).Style(bold ? TextStyle.Default.Bold() : TextStyle.Default);
                        row.AutoItem().Text(value).Style((bold ? TextStyle.Default.Bold() : TextStyle.Default).FontColor(color ?? Ink));
                    });
                    Row("Subtotal", Money(r.Subtotal));
                    if (r.DiscountAmount > 0)
                        Row(string.IsNullOrWhiteSpace(r.DiscountCode) ? "Discount" : $"Discount ({r.DiscountCode})", "- " + Money(r.DiscountAmount));
                    if (r.DeliveryCharge > 0)
                        Row("Delivery", Money(r.DeliveryCharge));
                    c.Item().PaddingVertical(3).LineHorizontal(1).LineColor(Line);
                    Row("TOTAL", Money(r.Total), bold: true, color: Accent);
                });

                if (r.Fields is { Count: > 0 })
                    col.Item().PaddingTop(8).Column(c =>
                    {
                        foreach (var field in r.Fields)
                            c.Item().Text(t => { t.Span($"{field.Name}: ").Bold(); t.Span(field.Value); });
                    });

                col.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(c =>
                {
                    c.Item().Text(t =>
                    {
                        t.Span("Payment: ").Bold();
                        t.Span($"{r.PaymentMethod} — ");
                        var partly = !r.Paid && r.AmountPaid > 0;
                        t.Span(r.Paid ? "PAID" : partly ? "PARTLY PAID" : "UNPAID").Bold().FontColor(r.Paid ? Accent : Colors.Red.Darken2);
                    });
                    if (!r.Paid && r.AmountPaid > 0)
                        c.Item().Text(t =>
                        {
                            t.Span($"Paid: {Money(r.AmountPaid)} · ");
                            t.Span($"Balance due: {Money(Math.Max(0, r.Total - r.AmountPaid))}").Bold();
                        });
                    c.Item().Text(t => { t.Span("Order status: ").Bold(); t.Span(r.Status); });
                    if (!string.IsNullOrWhiteSpace(r.TrackingNumber))
                        c.Item().Text(t => { t.Span("Tracking: ").Bold(); t.Span($"{r.TrackingCourier} {r.TrackingNumber}".Trim()); });
                    if (!r.Paid && r.PayTo.Count > 0)
                    {
                        c.Item().PaddingTop(4).Text("Pay to:").Bold();
                        foreach (var p in r.PayTo) c.Item().Text(p);
                    }
                });
            });

            page.Footer().AlignCenter().Text($"Thank you for shopping with {r.BusinessName}!").FontSize(9).FontColor(Muted);
        })).GeneratePdf();
    }
}
