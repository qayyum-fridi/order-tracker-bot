using System.Globalization;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Formatting;

public static class Formatters
{
    public static string Money(decimal amount) => "Rs." + amount.ToString("#,0.##", CultureInfo.InvariantCulture);

    public static string Status(OrderStatus status) => status switch
    {
        OrderStatus.Pending => "PENDING",
        OrderStatus.Shipped => "SHIPPED",
        OrderStatus.Delivered => "DELIVERED",
        OrderStatus.Cancelled => "CANCELLED",
        OrderStatus.Returned => "RETURNED",
        _ => status.ToString()
    };

    public static string ProductLabel(Product product)
    {
        var variant = string.Join(", ", new[] { product.Color, product.Size }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var label = variant.Length == 0 ? product.Name : $"{product.Name} ({variant})";
        return product.UnitType is "piece" or "" && product.UnitQty == 1 ? label : $"{label} - {Quantity(product.UnitQty)}{UnitShort(product.UnitType)}";
    }

    public static string Quantity(decimal qty) => qty.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Suffix after a quantity: 5 + "kg" = "5kg", 2 + " dozen" = "2 dozen".</summary>
    public static string UnitShort(string unitType) => unitType switch
    {
        "kg" => "kg",
        "gram" => "g",
        "liter" => "L",
        "meter" => "m",
        "piece" or "" => " pc",
        _ => " " + unitType
    };

    public static string SourceLabel(string source) => source.ToLowerInvariant() switch
    {
        "tiktok" => "TikTok",
        "instagram" => "Instagram",
        "whatsapp" => "WhatsApp",
        "facebook" => "Facebook",
        "referral" => "Referral",
        var s => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s)
    };

    public static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" }
    };

    public static string DaysAgo(DateTime utc)
    {
        var days = (DateTime.UtcNow.Date - utc.Date).Days;
        return days switch { <= 0 => "today", 1 => "yesterday", _ => MessageTemplates.Get("fmt-formatters.001", days) };
    }

    public static string ItemsSummary(Order order) =>
        string.Join(" + ", order.Items.Select(i => i.Quantity > 1
            ? $"{i.Quantity}x {i.ProductNameSnapshot}"
            : i.ProductNameSnapshot));

    public static string Status(string language, OrderStatus status) =>
        Lang.Normalize(language) == Lang.UrduScript
            ? status switch
            {
                OrderStatus.Pending => MessageTemplates.Get("fmt-formatters.002"),
                OrderStatus.Shipped => MessageTemplates.Get("fmt-formatters.003"),
                OrderStatus.Delivered => MessageTemplates.Get("fmt-formatters.004"),
                OrderStatus.Cancelled => "منسوخ",
                OrderStatus.Returned => "واپس آ گیا",
                _ => Status(status)
            }
            : Status(status);

    // Numerals stay Western digits even in Urdu script (matches real Pakistani usage); only words are translated.
    public static string OrderLine(string language, int index, Order order)
    {
        var status = Status(language, order.Status);
        return Lang.Normalize(language) == Lang.UrduScript
            ? $"{index}- {order.Customer?.Name} - {ItemsSummary(order)} - {Money(order.Total)} - {status}"
            : $"{index}. {order.Customer?.Name} - {ItemsSummary(order)} - {Money(order.Total)} - {status}";
    }

    public static string OrdersToday(string language, IReadOnlyList<Order> orders, string? period = null, string? periodLabel = null, bool deliveryDue = false)
    {
        var lang = Lang.Normalize(language);
        // Any period beyond today / yesterday / last month (this week, last quarter, 1 May - 15 May, tomorrow...) is labelled by its own name.
        var custom = periodLabel is not null && period is not (null or "yesterday" or "lastmonth");
        if (custom && orders.Count == 0)
            return deliveryDue ? $"📅 {periodLabel}: koi delivery due nahi."
                : lang == Lang.UrduScript ? $"📦 {periodLabel}: کوئی آرڈر نہیں۔" : $"📦 {periodLabel}: koi order nahi mila.";

        if (orders.Count == 0)
            return (lang, period) switch
            {
                (Lang.UrduScript, "yesterday") => MessageTemplates.Get("fmt-formatters.005"),
                (Lang.UrduScript, "lastmonth") => MessageTemplates.Get("fmt-formatters.006"),
                (Lang.UrduScript, _) => MessageTemplates.Get("fmt-formatters.007"),
                (Lang.English, "yesterday") => MessageTemplates.Get("fmt-formatters.008"),
                (Lang.English, "lastmonth") => MessageTemplates.Get("fmt-formatters.009"),
                (Lang.English, _) => MessageTemplates.Get("fmt-formatters.010"),
                (_, "yesterday") => MessageTemplates.Get("fmt-formatters.011"),
                (_, "lastmonth") => MessageTemplates.Get("fmt-formatters.012"),
                _ => MessageTemplates.Get("fmt-formatters.013")
            };

        var header = custom
            ? deliveryDue ? $"📅 Delivery due — {periodLabel} ({orders.Count}):"
                : lang == Lang.UrduScript ? MessageTemplates.Get("fmt-formatters.020", periodLabel, orders.Count) : $"📦 Orders — {periodLabel} ({orders.Count}):"
            : (lang, period) switch
        {
            (Lang.UrduScript, "yesterday") => MessageTemplates.Get("fmt-formatters.014", orders.Count),
            (Lang.UrduScript, "lastmonth") => MessageTemplates.Get("fmt-formatters.015", orders.Count),
            (Lang.UrduScript, _) => MessageTemplates.Get("fmt-formatters.016", orders.Count),
            (_, "yesterday") => $"📦 Yesterday's Orders ({orders.Count}):",
            (_, "lastmonth") => MessageTemplates.Get("fmt-formatters.017", orders.Count),
            _ => $"📦 Today's Orders ({orders.Count}):"
        };
        var footer = lang switch
        {
            Lang.UrduScript => MessageTemplates.Get("fmt-formatters.018"),
            _ => MessageTemplates.Get("fmt-formatters.019")
        };
        var lines = orders.Select((o, i) => OrderLine(language, i + 1, o));
        return $"{header}\n\n{string.Join("\n", lines)}\n\n{footer}";
    }

    public static string ReturningGreeting(string language, string? businessName, int pending, int unpaid) =>
        Lang.Normalize(language) switch
        {
            Lang.UrduScript => $"سلام {businessName}! 👋 واپس آنے پر خوشی ہوئی۔\n\n" +
                               $"Quick stats: {pending} pending orders, {unpaid} unpaid۔\n" +
                               "\"menu\" لکھیں options کے لیے، یا سیدھا order بھیج دیں۔",
            Lang.English => $"Welcome back, {businessName}! 👋\n\n" +
                            $"Quick stats: {pending} pending orders, {unpaid} unpaid.\n" +
                            "Type \"menu\" for options, or forward an order directly.",
            _ => $"Salam {businessName}! 👋 Wapas aane par khushi hui.\n\n" +
                 $"Quick stats: {pending} pending orders, {unpaid} unpaid.\n" +
                 "Type \"menu\" for options, ya seedha order bhej dein."
        };

    public static string OrderLine(int index, Order order) =>
        $"{index} {order.Customer?.Name} - {ItemsSummary(order)} - {Money(order.Total)} - {Status(order.Status)}";
}
