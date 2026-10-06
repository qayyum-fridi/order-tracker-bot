using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Formatting;

/// <summary>What a buyer has paid and still owes. PaymentStatus.Paid always means fully paid (also for orders paid before AmountPaid existed).</summary>
public static class OrderMoney
{
    public static decimal Received(Order order) => order.PaymentStatus == PaymentStatus.Paid ? order.Total : Math.Min(order.AmountPaid, order.Total);

    public static decimal Balance(Order order) => order.PaymentStatus == PaymentStatus.Paid ? 0 : Math.Max(0, order.Total - order.AmountPaid);

    public static string State(Order order) =>
        order.PaymentStatus == PaymentStatus.Paid ? "PAID"
        : order.AmountPaid > 0 ? $"PARTLY PAID — {Formatters.Money(Received(order))} mila, baqi {Formatters.Money(Balance(order))}"
        : "UNPAID";
}
