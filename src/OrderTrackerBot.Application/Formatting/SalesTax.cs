using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Formatting;

/// <summary>
/// Sales tax is treated as already included in the seller's prices (the normal way Pakistani retail quotes), so an order's
/// Total never changes: the tax is extracted from it for the invoice and reports. Delivery is part of the supply and is taxed too.
/// </summary>
public static class SalesTax
{
    /// <summary>Tax contained in a tax-inclusive <paramref name="total"/> at <paramref name="ratePercent"/> (18 -> total * 18/118).</summary>
    public static decimal Amount(decimal total, decimal ratePercent) =>
        ratePercent <= 0 ? 0 : Math.Round(total * ratePercent / (100m + ratePercent), 2, MidpointRounding.AwayFromZero);

    public static decimal Amount(Order order) => Amount(order.Total, order.SalesTaxRate);

    /// <summary>What the seller keeps after the courier / gateway withheld income tax from the payout.</summary>
    public static decimal NetOfWithheld(Order order) => Math.Max(0, order.Total - order.TaxWithheld);

    /// <summary>"18" for 18.00, "17.5" for 17.50.</summary>
    public static string Percent(decimal rate) => rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
