using System.Globalization;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

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
        _ => status.ToString()
    };

    public static string ProductLabel(Product product)
    {
        var variant = string.Join(", ", new[] { product.Color, product.Size }.Where(v => !string.IsNullOrWhiteSpace(v)));
        return variant.Length == 0 ? product.Name : $"{product.Name} ({variant})";
    }

    public static string ItemsSummary(Order order) =>
        string.Join(" + ", order.Items.Select(i => i.Quantity > 1
            ? $"{i.Quantity}x {i.ProductNameSnapshot}"
            : i.ProductNameSnapshot));

    public static string Status(string language, OrderStatus status) =>
        Lang.Normalize(language) == Lang.UrduScript
            ? status switch
            {
                OrderStatus.Pending => "زیر التواء",
                OrderStatus.Shipped => "بھیج دیا گیا",
                OrderStatus.Delivered => "پہنچا دیا گیا",
                OrderStatus.Cancelled => "منسوخ",
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

    public static string OrdersToday(string language, IReadOnlyList<Order> orders)
    {
        var lang = Lang.Normalize(language);
        if (orders.Count == 0)
            return lang switch
            {
                Lang.UrduScript => "📦 آج کوئی آرڈر نہیں آیا۔",
                Lang.English => "📦 No orders yet today.",
                _ => "📦 Aaj koi order nahi aaya abhi tak."
            };

        var header = lang == Lang.UrduScript ? $"📦 آج کے آرڈرز ({orders.Count}):" : $"📦 Today's Orders ({orders.Count}):";
        var footer = lang switch
        {
            Lang.UrduScript => "\"mark 1 shipped\" لکھ کر اپڈیٹ کریں۔",
            _ => "Reply \"mark 1 shipped\" to update."
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
