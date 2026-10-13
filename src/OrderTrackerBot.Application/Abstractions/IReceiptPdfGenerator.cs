namespace OrderTrackerBot.Application.Abstractions;

public sealed record ReceiptLine(string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <summary>Everything a printed order receipt shows. Dates are already in the seller's local time.</summary>
public sealed record ReceiptData(
    int OrderId,
    DateTime OrderedAtLocal,
    string BusinessName,
    string? BusinessPhone,
    string? BusinessCity,
    string? InstagramHandle,
    string CustomerName,
    string? CustomerPhone,
    string? CustomerAddress,
    IReadOnlyList<ReceiptLine> Lines,
    decimal Subtotal,
    decimal DiscountAmount,
    string? DiscountCode,
    decimal Total,
    string PaymentMethod,
    bool Paid,
    string Status,
    string? TrackingCourier,
    string? TrackingNumber,
    IReadOnlyList<string> PayTo,
    byte[]? Logo = null,
    byte[]? Banner = null,
    decimal DeliveryCharge = 0,
    decimal AmountPaid = 0,
    int? ReceiptNumber = null,
    string? Ntn = null,
    string? Strn = null,
    decimal SalesTaxRate = 0,
    decimal SalesTaxAmount = 0,
    string PaperSize = "A5",
    string? Footer = null,
    string? Notes = null,
    ReceiptDesign? Design = null);

/// <summary>A parcel label: who it is from and to, what to collect on delivery, the order number and tracking.</summary>
public sealed record ShippingLabelData(
    int OrderId,
    string BusinessName,
    string? BusinessPhone,
    string? BusinessCity,
    string CustomerName,
    string? CustomerPhone,
    string? CustomerAddress,
    string ItemsSummary,
    bool IsCod,
    decimal CodAmount,
    string? TrackingCourier,
    string? TrackingNumber,
    string? Notes,
    string LabelSize = "4x6");

/// <summary>A sticker with the shop's own details (logo, name, phone, Instagram) for packaging.</summary>
public sealed record BusinessLabelData(
    string BusinessName,
    string? Phone,
    string? City,
    string? InstagramHandle,
    string? Tagline,
    byte[]? Logo = null,
    string LabelSize = "4x6");

public interface IReceiptPdfGenerator
{
    byte[] Generate(ReceiptData receipt);

    /// <summary>A shipping label ("4x6", "A6"; a "50x30" sticker is too small for an address and falls back to 4x6).</summary>
    byte[] GenerateShippingLabel(ShippingLabelData label);

    /// <summary>A business sticker ("4x6", "A6" or "50x30").</summary>
    byte[] GenerateBusinessLabel(BusinessLabelData label);

    /// <summary>True when <paramref name="image"/> is a PNG/JPEG/WebP the PDF can embed (guards the receipt against bad uploads).</summary>
    bool CanEmbedImage(byte[] image);
}
