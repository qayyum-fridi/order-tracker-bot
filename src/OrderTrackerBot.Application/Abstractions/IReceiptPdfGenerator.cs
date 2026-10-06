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
    decimal DeliveryCharge = 0);

public interface IReceiptPdfGenerator
{
    byte[] Generate(ReceiptData receipt);

    /// <summary>True when <paramref name="image"/> is a PNG/JPEG/WebP the PDF can embed (guards the receipt against bad uploads).</summary>
    bool CanEmbedImage(byte[] image);
}
