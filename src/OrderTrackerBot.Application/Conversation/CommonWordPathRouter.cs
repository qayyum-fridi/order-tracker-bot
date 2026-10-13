using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>The business area a message's common words point at.</summary>
public enum IntendedPath
{
    Catalog,
    Order,
    Customer,
    Shipment,
    Payment
}

/// <summary>
/// Deterministic keyword check (no AI): which business areas does a message talk about? Used to let the seller leave the
/// "waiting for the price of the new products" step when they clearly ask for something else ("Kashif ka naya order likho").
/// Words are matched as whole tokens (Roman Urdu, English and Urdu script); multi-word entries match as a phrase.
/// </summary>
public static class CommonWordPathRouter
{
    private static readonly Dictionary<IntendedPath, string[]> Lexicon = new()
    {
        [IntendedPath.Catalog] = new[]
        {
            "catalog", "catalogue", "item", "product", "products", "rate", "price", "stock", "wholesale", "retail",
            "maal", "saman", "rate list", "keemat", "qeemat", "bhao", "katalog",
            "آئٹم", "پروڈکٹ", "مال", "سٹاک", "قیمت", "ریٹ", "کیٹلاگ"
        },
        [IntendedPath.Order] = new[]
        {
            "order", "bill", "invoice", "buy", "quantity", "pieces", "size", "color",
            "naya order", "sauda", "grahak", "suit", "jora", "likho",
            "آرڈر", "بل", "سوٹ", "پیس", "جوڑا", "لکھو"
        },
        [IntendedPath.Customer] = new[]
        {
            "customer", "address", "phone", "number", "location", "city", "street", "house",
            "naam", "pata", "gali", "shehar", "rabta",
            "نام", "پتہ", "گلی", "شہر", "فون نمبر", "ایڈریس"
        },
        [IntendedPath.Shipment] = new[]
        {
            "shipped", "delivered", "dispatch", "tracking", "rider", "courier",
            "bhej diya", "ravana", "pohnch gaya", "pahunch gaya",
            "بھیج دیا", "روانہ", "شپ", "ڈیلیوری"
        },
        [IntendedPath.Payment] = new[]
        {
            "advance", "cod", "cash", "payment", "received", "profit", "expense", "loss", "wages",
            "baki", "udhaar", "udhar", "khata", "paisa", "raseed", "nuqsan", "dihari",
            "ایڈوانس", "کیش", "ادھار", "کھاتا", "پیسے", "رسید"
        }
    };

    private static readonly Regex Token = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<IntendedPath, string[]> Padded = Lexicon.ToDictionary(
        kv => kv.Key, kv => kv.Value.Select(w => " " + w.ToLowerInvariant() + " ").ToArray());

    /// <summary>Every area that has at least one of its words in the message.</summary>
    public static IReadOnlySet<IntendedPath> Detect(string? text)
    {
        var found = new HashSet<IntendedPath>();
        if (string.IsNullOrWhiteSpace(text)) return found;
        var padded = " " + string.Join(' ', Token.Matches(text.ToLowerInvariant()).Select(m => m.Value)) + " ";
        foreach (var (path, words) in Padded)
            if (words.Any(w => padded.Contains(w, StringComparison.Ordinal))) found.Add(path);
        return found;
    }

    /// <summary>
    /// True when a message sent while the bot waits for new products' prices is clearly about something else: it names an order or a
    /// payment, no catalog word, and it holds no amount (an amount is the price answer, even when the product is called "suit").
    /// </summary>
    public static bool ShouldLeavePriceWait(string? text)
    {
        var areas = Detect(text);
        if (areas.Contains(IntendedPath.Catalog)) return false;
        if (!areas.Contains(IntendedPath.Order) && !areas.Contains(IntendedPath.Payment)) return false;
        return SpokenNumbers.Amounts(text!).Count == 0;
    }
}
