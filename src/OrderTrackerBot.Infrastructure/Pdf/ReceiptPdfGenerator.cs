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

    private static string? Id(string label, string? value) => string.IsNullOrWhiteSpace(value) ? null : $"{label} {value}";

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

    private static PageSize LabelPage(string? size) => size?.Trim().ToLowerInvariant() switch
    {
        "a6" => PageSizes.A6,
        "50x30" => new PageSize(50, 30, Unit.Millimetre),
        _ => new PageSize(101.6f, 152.4f, Unit.Millimetre) // 4 x 6 inch: the standard courier label
    };

    public byte[] GenerateShippingLabel(ShippingLabelData d)
    {
        // A 50x30mm sticker cannot hold an address: use the standard label instead.
        var size = string.Equals(d.LabelSize, "50x30", StringComparison.OrdinalIgnoreCase) ? "4x6" : d.LabelSize;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(LabelPage(size));
            page.Margin(5, Unit.Millimetre);
            page.DefaultTextStyle(Base.FontSize(11).FontColor(Colors.Black));

            page.Content().Border(1.5f).Padding(8).Column(col =>
            {
                col.Spacing(5);
                col.Item().Text("FROM").FontSize(8).Bold();
                col.Item().Text(d.BusinessName).FontSize(12).Bold();
                var from = string.Join(" · ", new[] { d.BusinessPhone, d.BusinessCity }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (from.Length > 0) col.Item().Text(from).FontSize(10);
                col.Item().LineHorizontal(1);

                col.Item().Text("TO").FontSize(9).Bold();
                col.Item().Text(d.CustomerName).FontSize(20).Bold();
                if (!string.IsNullOrWhiteSpace(d.CustomerPhone)) col.Item().Text(d.CustomerPhone).FontSize(16).Bold();
                if (!string.IsNullOrWhiteSpace(d.CustomerAddress)) col.Item().Text(d.CustomerAddress).FontSize(14);
                col.Item().LineHorizontal(1);

                col.Item().Row(row =>
                {
                    row.RelativeItem().Text($"Order #{d.OrderId}").FontSize(16).Bold();
                    row.AutoItem().Text(d.IsCod ? $"COD {Money(d.CodAmount)}" : "PREPAID").FontSize(16).Bold();
                });
                if (!string.IsNullOrWhiteSpace(d.ItemsSummary)) col.Item().Text(d.ItemsSummary).FontSize(10);
                if (!string.IsNullOrWhiteSpace(d.TrackingNumber))
                    col.Item().Text($"{d.TrackingCourier} {d.TrackingNumber}".Trim()).FontSize(11).Bold();
                if (!string.IsNullOrWhiteSpace(d.Notes)) col.Item().Text($"Note: {d.Notes}").FontSize(10);
            });
        })).GeneratePdf();
    }

    public byte[] GenerateBusinessLabel(BusinessLabelData d)
    {
        try
        {
            return RenderBusinessLabel(d);
        }
        catch when (d.Logo is not null)
        {
            return RenderBusinessLabel(d with { Logo = null }); // a logo the PDF engine cannot draw must not cost the seller the label
        }
    }

    private static byte[] RenderBusinessLabel(BusinessLabelData d)
    {
        var small = string.Equals(d.LabelSize, "50x30", StringComparison.OrdinalIgnoreCase);
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(LabelPage(d.LabelSize));
            page.Margin(small ? 2 : 6, Unit.Millimetre);
            page.DefaultTextStyle(Base.FontSize(small ? 7 : 12).FontColor(Colors.Black));

            page.Content().AlignMiddle().AlignCenter().Column(col =>
            {
                col.Spacing(small ? 1 : 6);
                if (d.Logo is not null) col.Item().AlignCenter().Height(small ? 22 : 90).Image(d.Logo).FitArea();
                col.Item().AlignCenter().Text(d.BusinessName).FontSize(small ? 11 : 26).Bold();
                if (!string.IsNullOrWhiteSpace(d.City)) col.Item().AlignCenter().Text(d.City);
                if (!string.IsNullOrWhiteSpace(d.Phone)) col.Item().AlignCenter().Text($"WhatsApp: {d.Phone}").FontSize(small ? 8 : 14).Bold();
                if (!string.IsNullOrWhiteSpace(d.InstagramHandle)) col.Item().AlignCenter().Text(d.InstagramHandle);
                if (!string.IsNullOrWhiteSpace(d.Tagline)) col.Item().AlignCenter().Text(d.Tagline).Italic();
            });
        })).GeneratePdf();
    }

    /// <summary>Roll width in mm for "80mm" / "58mm" (thermal printers); null for the sheet sizes.</summary>
    private static float? ThermalWidthMm(string? paperSize) => paperSize?.Trim().ToLowerInvariant() switch
    {
        "80mm" => 80f,
        "58mm" => 58f,
        _ => null
    };

    /// <summary>
    /// Thermal roll receipt: one narrow column, continuous height (the page is as long as the content, so nothing is cut or wasted),
    /// each item on two lines (name, then qty x price and amount) because a 58mm roll has no room for a table. Plain black on white: thermal heads print no grey.
    /// </summary>
    private static byte[] RenderThermal(ReceiptData r, float widthMm)
    {
        var design = r.Design ?? new ReceiptDesign();
        var size = (widthMm < 70f ? 8f : 9f) + TextSizeDelta(design);
        return Document.Create(doc => doc.Page(page =>
        {
            page.ContinuousSize(widthMm, Unit.Millimetre);
            page.Margin(3, Unit.Millimetre);
            page.DefaultTextStyle(Base.FontSize(size).FontColor(Colors.Black));

            page.Content().Column(col =>
            {
                col.Spacing(3);
                if (r.Logo is not null && !design.HideLogo) col.Item().AlignCenter().Height(32).Image(r.Logo).FitArea();
                col.Item().AlignCenter().Text(r.BusinessName).FontSize(size + 3).Bold();
                if (!string.IsNullOrWhiteSpace(r.Header)) col.Item().AlignCenter().Text(r.Header).FontSize(size - 1).Italic();
                var sub = string.Join(" · ", new[] { r.BusinessCity, design.HideShopPhone ? null : r.BusinessPhone }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (sub.Length > 0) col.Item().AlignCenter().Text(sub).FontSize(size - 1);
                if (!string.IsNullOrWhiteSpace(r.Contact)) col.Item().AlignCenter().Text(r.Contact).FontSize(size - 1);
                var taxIds = string.Join(" · ", new[] { Id("NTN", r.Ntn), Id("STRN", r.Strn) }.Where(s => s is not null));
                if (taxIds.Length > 0) col.Item().AlignCenter().Text(taxIds).FontSize(size - 1);
                col.Item().AlignCenter().Text(r.SalesTaxRate > 0 ? "TAX INVOICE" : (design.Title ?? "RECEIPT").ToUpperInvariant()).Bold();
                col.Item().AlignCenter().Text((r.ReceiptNumber is { } no ? $"No. {no} · " : "") + $"Order #{r.OrderId}");
                col.Item().AlignCenter().Text(r.OrderedAtLocal.ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture)).FontSize(size - 1);
                col.Item().LineHorizontal(0.5f);

                col.Item().Column(c =>
                {
                    c.Item().Text(r.CustomerName).Bold();
                    if (!design.HideBuyerDetails && !string.IsNullOrWhiteSpace(r.CustomerPhone)) c.Item().Text(r.CustomerPhone);
                    if (!design.HideBuyerDetails && !string.IsNullOrWhiteSpace(r.CustomerAddress)) c.Item().Text(r.CustomerAddress);
                });
                col.Item().LineHorizontal(0.5f);

                foreach (var item in r.Lines)
                    col.Item().Column(c =>
                    {
                        c.Item().Text(item.Name);
                        c.Item().Row(row =>
                        {
                            row.RelativeItem().Text($"{item.Quantity.ToString(CultureInfo.InvariantCulture)} x {Money(item.UnitPrice)}");
                            row.AutoItem().Text(Money(item.LineTotal));
                        });
                    });
                col.Item().LineHorizontal(0.5f);

                void Row(string label, string value, bool bold = false) => col.Item().Row(row =>
                {
                    row.RelativeItem().Text(label).Style(bold ? TextStyle.Default.Bold() : TextStyle.Default);
                    row.AutoItem().Text(value).Style(bold ? TextStyle.Default.Bold() : TextStyle.Default);
                });
                Row("Subtotal", Money(r.Subtotal));
                if (r.DiscountAmount > 0)
                    Row(string.IsNullOrWhiteSpace(r.DiscountCode) ? "Discount" : $"Discount ({r.DiscountCode})", "- " + Money(r.DiscountAmount));
                if (r.DeliveryCharge > 0) Row("Delivery", Money(r.DeliveryCharge));
                if (r.SalesTaxRate > 0)
                    Row($"Sales tax ({r.SalesTaxRate.ToString("0.##", CultureInfo.InvariantCulture)}%)", Money(r.SalesTaxAmount));
                Row(r.SalesTaxRate > 0 ? "TOTAL (incl. tax)" : "TOTAL", Money(r.Total), bold: true);

                var partly = !r.Paid && r.AmountPaid > 0;
                col.Item().Text($"{r.PaymentMethod}: {(r.Paid ? "PAID" : partly ? "PARTLY PAID" : "UNPAID")}").Bold();
                if (partly)
                {
                    Row("Paid", Money(r.AmountPaid));
                    Row("Balance due", Money(Math.Max(0, r.Total - r.AmountPaid)), bold: true);
                }
                if (!string.IsNullOrWhiteSpace(r.TrackingNumber)) col.Item().Text($"Tracking: {r.TrackingCourier} {r.TrackingNumber}".Replace("  ", " ").Trim());
                if (!r.Paid && r.PayTo.Count > 0)
                {
                    col.Item().Text("Pay to:").Bold();
                    foreach (var p in r.PayTo) col.Item().Text(p);
                }
                if (!string.IsNullOrWhiteSpace(r.Notes)) col.Item().Text($"Note: {r.Notes}");

                col.Item().LineHorizontal(0.5f);
                col.Item().AlignCenter().Text(string.IsNullOrWhiteSpace(r.Footer) ? $"Thank you for shopping with {r.BusinessName}!" : r.Footer).FontSize(size - 1);
            });
        })).GeneratePdf();
    }

    private static float TextSizeDelta(ReceiptDesign d) => d.TextSize switch { "small" => -1f, "large" => 1.5f, _ => 0f };

    private static byte[] Render(ReceiptData r)
    {
        if (ThermalWidthMm(r.PaperSize) is { } thermalWidth) return RenderThermal(r, thermalWidth);
        var design = r.Design ?? new ReceiptDesign();
        var accent = design.Accent ?? Accent;
        var title = r.SalesTaxRate > 0 ? "TAX INVOICE" : (design.Title ?? "RECEIPT").ToUpperInvariant();

        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(string.Equals(r.PaperSize, "A4", StringComparison.OrdinalIgnoreCase) ? PageSizes.A4 : PageSizes.A5);
            page.Margin(28);
            page.DefaultTextStyle(Base.FontSize(10 + TextSizeDelta(design)));

            page.Header().Column(col =>
            {
                if (r.Banner is not null)
                    col.Item().PaddingBottom(8).AlignCenter().MaxHeight(90).Image(r.Banner).FitArea();
                var showLogo = r.Logo is not null && !design.HideLogo;
                void ShopBlock(IContainer box) => box.Column(c =>
                {
                    c.Item().Text(r.BusinessName).FontSize(16).Bold().FontColor(accent);
                    if (!string.IsNullOrWhiteSpace(r.Header)) c.Item().Text(r.Header).FontSize(10).Italic().FontColor(Ink);
                    var sub = string.Join(" · ", new[] { r.BusinessCity, design.HideShopPhone || r.BusinessPhone is null ? null : "WhatsApp " + r.BusinessPhone, r.InstagramHandle }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (sub.Length > 0) c.Item().Text(sub).FontSize(9).FontColor(Muted);
                    if (!string.IsNullOrWhiteSpace(r.Contact)) c.Item().Text(r.Contact).FontSize(9).FontColor(Muted);
                    var taxIds = string.Join(" · ", new[] { Id("NTN", r.Ntn), Id("STRN", r.Strn) }.Where(s => s is not null));
                    if (taxIds.Length > 0) c.Item().Text(taxIds).FontSize(9).FontColor(Muted);
                });
                void DocBlock(IContainer box, bool right) => box.Column(c =>
                {
                    IContainer Line(IContainer x) => right ? x.AlignRight() : x.AlignCenter();
                    c.Item().Element(Line).Text(title).FontSize(14).Bold().FontColor(design.Accent is null ? Ink : accent);
                    c.Item().Element(Line).Text(r.ReceiptNumber is { } no ? $"No. {no}" : $"#{r.OrderId}").FontSize(11);
                    if (r.ReceiptNumber is not null) c.Item().Element(Line).Text($"Order #{r.OrderId}").FontSize(9).FontColor(Muted);
                    c.Item().Element(Line).Text(r.OrderedAtLocal.ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture)).FontSize(9).FontColor(Muted);
                });

                if (design.Centered)
                    col.Item().Column(c =>
                    {
                        c.Spacing(4);
                        if (showLogo) c.Item().AlignCenter().Height(48).Image(r.Logo!).FitArea();
                        c.Item().AlignCenter().Element(b => ShopBlock(b));
                        c.Item().AlignCenter().Element(b => DocBlock(b, false));
                    });
                else
                    col.Item().Row(row =>
                    {
                        if (showLogo)
                            row.ConstantItem(56).PaddingRight(8).Height(48).Image(r.Logo!).FitArea();
                        row.RelativeItem().Element(b => ShopBlock(b));
                        row.ConstantItem(120).AlignRight().Element(b => DocBlock(b, true));
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
                    if (!design.HideBuyerDetails && !string.IsNullOrWhiteSpace(r.CustomerPhone)) c.Item().Text(r.CustomerPhone);
                    if (!design.HideBuyerDetails && !string.IsNullOrWhiteSpace(r.CustomerAddress)) c.Item().Text(r.CustomerAddress);
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
                        table.Cell().Element(Body).Text(item.Name);
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
                    if (r.SalesTaxRate > 0)
                    {
                        Row("Value excl. sales tax", Money(r.Total - r.SalesTaxAmount));
                        Row($"Sales tax ({r.SalesTaxRate.ToString("0.##", CultureInfo.InvariantCulture)}%)", Money(r.SalesTaxAmount));
                    }
                    Row(r.SalesTaxRate > 0 ? "TOTAL (incl. tax)" : "TOTAL", Money(r.Total), bold: true, color: accent);
                    if (r.SalesTaxRate > 0)
                        c.Item().PaddingTop(3).Text("Prices include sales tax.").FontSize(8).FontColor(Muted);
                });

                col.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(c =>
                {
                    c.Item().Text(t =>
                    {
                        t.Span("Payment: ").Bold();
                        t.Span($"{r.PaymentMethod} — ");
                        var partly = !r.Paid && r.AmountPaid > 0;
                        t.Span(r.Paid ? "PAID" : partly ? "PARTLY PAID" : "UNPAID").Bold().FontColor(r.Paid ? accent : Colors.Red.Darken2);
                    });
                    if (!r.Paid && r.AmountPaid > 0)
                        c.Item().Text(t =>
                        {
                            t.Span($"Paid: {Money(r.AmountPaid)} · ");
                            t.Span($"Balance due: {Money(Math.Max(0, r.Total - r.AmountPaid))}").Bold();
                        });
                    if (!design.HideOrderStatus) c.Item().Text(t => { t.Span("Order status: ").Bold(); t.Span(r.Status); });
                    if (!string.IsNullOrWhiteSpace(r.Notes))
                        c.Item().Text(t => { t.Span("Note: ").Bold(); t.Span(r.Notes); });
                    if (!string.IsNullOrWhiteSpace(r.TrackingNumber))
                        c.Item().Text(t => { t.Span("Tracking: ").Bold(); t.Span($"{r.TrackingCourier} {r.TrackingNumber}".Trim()); });
                    if (!r.Paid && r.PayTo.Count > 0)
                    {
                        c.Item().PaddingTop(4).Text("Pay to:").Bold();
                        foreach (var p in r.PayTo) c.Item().Text(p);
                    }
                });
            });

            page.Footer().AlignCenter().Text(string.IsNullOrWhiteSpace(r.Footer) ? $"Thank you for shopping with {r.BusinessName}!" : r.Footer).FontSize(9).FontColor(Muted);
        })).GeneratePdf();
    }
}
