using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

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
            await ReplyAsync(seller, $"Order #{number} nahi mila.", ct);
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
        lines.Add($"💰 {PaymentLabel(order.PaymentMethod, null)} — {OrderMoney.State(order)}");
        if (!string.IsNullOrWhiteSpace(order.TrackingNumber)) lines.Add($"🚚 {order.TrackingCourier} {order.TrackingNumber}".TrimEnd());
        lines.Add($"🗓️ {placed:dd MMM yyyy, hh:mm tt}" + (string.IsNullOrWhiteSpace(order.OrderSource) ? "" : $" · {Formatters.SourceLabel(order.OrderSource)}"));
        if (!string.IsNullOrWhiteSpace(order.Notes)) lines.Add($"📝 {order.Notes}");
        lines.AddRange(await CustomFieldLinesAsync(seller, CustomFieldEntity.Order, order.Id, ct));
        lines.Add("");
        lines.Add($"👉 \"edit order {order.Id}\" · \"receipt {order.Id}\" · \"mark {order.Id} shipped\"" +
                  (order.PaymentStatus == PaymentStatus.Paid ? "" : $" · \"order {order.Id} advance 500\""));
        await ReplyAsync(seller, string.Join("\n", lines), ct);
        await OfferFieldsButtonAsync(seller, ctx, CustomFieldEntity.Order, order.Id, $"Order #{order.Id}", ct);
    }

    private async Task HandleOrderPaymentAsync(Seller seller, SessionContextData ctx, int number, decimal amount, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, number);
        var order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, $"Order #{number} nahi mila.", ct);
            return;
        }
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await ReplyAsync(seller, $"Order #{order.Id} {Formatters.Status(order.Status)} hai — payment add nahi ho sakti.", ct);
            return;
        }
        if (order.PaymentStatus == PaymentStatus.Paid)
        {
            await ReplyAsync(seller, $"Order #{order.Id} pehle se poora PAID hai ({Formatters.Money(order.Total)}).", ct);
            return;
        }
        if (amount <= 0)
        {
            await ReplyAsync(seller, "Raqam 0 se zyada likhein, jaise \"order 12 advance 500\".", ct);
            return;
        }

        LogPaymentChange(seller, order);
        order.AmountPaid += amount;
        var note = fromList ? $"\nℹ️ \"{number}\" aapki last list ka number tha." : "";
        if (order.AmountPaid >= order.Total)
        {
            var extra = order.AmountPaid - order.Total;
            order.AmountPaid = order.Total;
            order.PaymentStatus = PaymentStatus.Paid;
            order.PaidAt = DateTime.UtcNow;
            await ReplyAsync(seller, $"✅ Order #{order.Id} ({order.Customer?.Name}): {Formatters.Money(amount)} mila — ab poora PAID." +
                (extra > 0 ? $"\n⚠️ {Formatters.Money(extra)} total se zyada aaye — wapas karne hon to yaad rakhein." : "") + note, ct);
            return;
        }

        await ReplyAsync(seller,
            $"✅ Order #{order.Id} ({order.Customer?.Name}): {Formatters.Money(amount)} mila.\n" +
            $"Ab tak {Formatters.Money(order.AmountPaid)} / {Formatters.Money(order.Total)} — baqi {Formatters.Money(OrderMoney.Balance(order))}." + note, ct);
    }
}
