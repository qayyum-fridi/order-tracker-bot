using System.Globalization;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Maps the product-line details the seller writes ("expiry: 2027-03", "model: X200") onto their own product columns.
/// A key that isn't one of these, or a value that doesn't read as that column's type, is not mapped and stays a plain detail.
/// </summary>
internal static class ProductTypedDetails
{
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM", "MM/yyyy" };

    /// <summary>Sets the column for <paramref name="key"/> when the value reads as that column's type. False = not a typed detail.</summary>
    public static bool TrySet(Product product, string key, string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return false;
        switch (key.Trim().ToLowerInvariant())
        {
            case "expiry":
            case "expiry date":
                if (!TryDate(text, out var expiry)) return false;
                product.ExpiryDate = expiry;
                return true;
            case "batch":
                product.BatchNumber = text;
                return true;
            case "warranty":
                if (!int.TryParse(text.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var months) || months < 0) return false;
                product.WarrantyMonths = months;
                return true;
            case "model":
                product.Model = text;
                return true;
            case "brand":
                product.Brand = text;
                return true;
            case "prep":
                if (!int.TryParse(text.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var prep) || prep < 0) return false;
                product.PrepMinutes = prep;
                return true;
            case "madetoorder":
                if (!TryYesNo(text, out var madeToOrder)) return false;
                product.MadeToOrder = madeToOrder;
                return true;
            case "allergens":
                product.Allergens = text;
                return true;
            case "dimensions":
                product.Dimensions = text;
                return true;
            case "agegroup":
                product.AgeGroup = text;
                return true;
            case "sizechart":
                product.SizeChart = text;
                return true;
            default:
                return false;
        }
    }

    /// <summary>A "yyyy-MM" or "MM/yyyy" month means the last day of that month: the stock may be sold until then.</summary>
    private static bool TryDate(string text, out DateTime date)
    {
        if (!DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
        if (text.Length <= 7 || text.Count(c => c == '/') == 1)
            date = new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
        return true;
    }

    private static bool TryYesNo(string text, out bool value)
    {
        switch (text.ToLowerInvariant())
        {
            case "yes": case "haan": case "true": value = true; return true;
            case "no": case "nahi": case "false": value = false; return true;
            default: value = false; return false;
        }
    }
}
