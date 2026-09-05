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

    public static string ItemsSummary(Order order) =>
        string.Join(" + ", order.Items.Select(i => i.Quantity > 1
            ? $"{i.Quantity}x {i.ProductNameSnapshot}"
            : i.ProductNameSnapshot));

    public static string OrderLine(int index, Order order) =>
        $"{index} {order.Customer?.Name} - {ItemsSummary(order)} - {Money(order.Total)} - {Status(order.Status)}";
}
