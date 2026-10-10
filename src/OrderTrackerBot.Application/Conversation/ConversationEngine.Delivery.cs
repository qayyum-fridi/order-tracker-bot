using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Delivery charges: a per-seller default added to every new order, changeable per order while confirming ("delivery 300")
// or after saving ("order 12 delivery 300"). Discounts never apply to delivery.
public partial class ConversationEngine
{
    private async Task HandleDeliveryChargeAsync(Seller seller, decimal? amount, CancellationToken ct)
    {
        if (amount is null)
        {
            await ReplyAsync(seller,
                $"🚚 Aapka delivery charge: {(seller.DefaultDeliveryCharge > 0 ? Formatters.Money(seller.DefaultDeliveryCharge) : "free (0)")} — har naye order mein lagta hai.\n\n" +
                "Badalne ke liye: \"delivery 200\" ya \"free delivery\"\nSirf aik order ka: \"order 12 delivery 300\"", ct);
            return;
        }

        seller.DefaultDeliveryCharge = amount.Value;
        await ReplyAsync(seller, amount.Value > 0
            ? $"✅ Delivery charge {Formatters.Money(amount.Value)} set — ab har naye order ke total mein shamil hoga.\nKisi aik order ka badalna ho: \"order 12 delivery 300\""
            : "✅ Free delivery — naye orders par delivery charge nahi lagega.", ct);
    }

    private async Task HandleOrderDeliveryChargeAsync(Seller seller, SessionContextData ctx, int number, decimal amount, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, number);
        var order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, $"Order #{number} nahi mila.", ct);
            return;
        }
        if (IsDispatched(order))
        {
            await ReplyAsync(seller, $"Order #{order.Id} {Formatters.Status(order.Status)} hai — delivery nahi badal sakti. Payment ke liye \"order {order.Id} advance 500\" likhein.", ct);
            return;
        }

        order.Total = OrderTotal(order.Subtotal, order.DiscountAmount, amount);
        order.DeliveryCharge = amount;
        await ReplyAsync(seller,
            $"✅ Order #{order.Id} ({order.Customer?.Name}) — delivery {(amount > 0 ? Formatters.Money(amount) : "free")}, naya total {Formatters.Money(order.Total)}." +
            (fromList ? $"\nℹ️ \"{number}\" aapki last list ka number tha." : ""), ct);
    }
}
