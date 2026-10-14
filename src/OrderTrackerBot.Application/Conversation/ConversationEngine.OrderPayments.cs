using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// "order 12" (full details) and "order 12 advance 500" (part payments that add up until the order is fully paid).
public partial class ConversationEngine
{
    private async Task HandleOrderDetailAsync(Seller seller, SessionContextData ctx, int number, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, number);
        var order = await LoadOrderForEditAsync(seller, orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.028", number), ct);
            return;
        }

        var tz = SellerClock.Resolve(seller.TimeZoneId);
        var placed = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(order.CreatedAt, DateTimeKind.Utc), tz);
        var lines = new List<string>
        {
            $"📦 Order #{order.Id} — {Formatters.Status(order.Status)}" + (fromList ? $" (list ka #{number})" : ""),
            $"👤 {order.Customer?.Name}" + (string.IsNullOrWhiteSpace(order.Customer?.Phone) ? "" : $" · {order.Customer!.Phone}")
        };
        if (!string.IsNullOrWhiteSpace(order.Customer?.Address)) lines.Add($"📍 {order.Customer!.Address}");
        lines.AddRange(order.Items.OrderBy(i => i.Id).Select((i, n) => $"{n + 1}. {i.ProductNameSnapshot} x{i.Quantity} — {Formatters.Money(i.UnitPrice * i.Quantity)}"));
        if (order.DiscountAmount > 0) lines.Add($"Discount{(order.DiscountCode is null ? "" : $" ({order.DiscountCode})")}: -{Formatters.Money(order.DiscountAmount)}");
        if (order.DeliveryCharge > 0) lines.Add($"Delivery: {Formatters.Money(order.DeliveryCharge)}");
        lines.Add($"Total: {Formatters.Money(order.Total)}");
        if (order.SalesTaxRate > 0) lines.Add($"Sales tax ({SalesTax.Percent(order.SalesTaxRate)}% shamil): {Formatters.Money(SalesTax.Amount(order))}");
        if (order.TaxWithheld > 0) lines.Add($"Tax withheld: {Formatters.Money(order.TaxWithheld)} → aap ko milenge {Formatters.Money(SalesTax.NetOfWithheld(order))}");
        if (order.ReceiptNumber is { } receiptNo) lines.Add($"🧾 {(order.SalesTaxRate > 0 ? "Invoice" : "Receipt")} No. {receiptNo}");
        lines.Add($"💰 {PaymentLabel(order.PaymentMethod, null)} — {OrderMoney.State(order)}");
        if (!string.IsNullOrWhiteSpace(order.TrackingNumber)) lines.Add($"🚚 {order.TrackingCourier} {order.TrackingNumber}".TrimEnd());
        lines.Add($"🗓️ {placed:dd MMM yyyy, hh:mm tt}" + (string.IsNullOrWhiteSpace(order.OrderSource) ? "" : $" · {Formatters.SourceLabel(order.OrderSource)}"));
        if (!string.IsNullOrWhiteSpace(order.Notes)) lines.Add($"📝 {order.Notes}");
        lines.Add("");
        lines.Add($"👉 \"edit order {order.Id}\" · \"receipt {order.Id}\" · \"status {order.Id}\"" +
                  (order.PaymentStatus == PaymentStatus.Paid ? "" : $" · \"order {order.Id} advance 500\""));
        await ReplyAsync(seller, string.Join("\n", lines), ct);
    }

    private async Task HandleOrderPaymentAsync(Seller seller, SessionContextData ctx, int number, decimal amount, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, number);
        var order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.028", number), ct);
            return;
        }
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-payments.001", order.Id, Formatters.Status(order.Status)), ct);
            return;
        }
        if (order.PaymentStatus == PaymentStatus.Paid)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-payments.002", order.Id, Formatters.Money(order.Total)), ct);
            return;
        }
        if (amount <= 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-payments.003"), ct);
            return;
        }

        LogPaymentChange(seller, order);
        order.AmountPaid += amount;
        var note = fromList ? MessageTemplates.Get("order-payments.006", number) : "";
        if (order.AmountPaid >= order.Total)
        {
            var extra = order.AmountPaid - order.Total;
            order.AmountPaid = order.Total;
            order.PaymentStatus = PaymentStatus.Paid;
            order.PaidAt = DateTime.UtcNow;
            await ReplyAsync(seller, MessageTemplates.Get("order-payments.004", order.Id, order.Customer?.Name, Formatters.Money(amount), (extra > 0 ? $"\n⚠️ {Formatters.Money(extra)} total se zyada aaye — wapas karne hon to yaad rakhein." : ""), note), ct);
            return;
        }

        await ReplyAsync(seller,
            MessageTemplates.Get("order-payments.005", order.Id, order.Customer?.Name, Formatters.Money(amount), Formatters.Money(order.AmountPaid), Formatters.Money(order.Total), Formatters.Money(OrderMoney.Balance(order)), note), ct);
    }
}
