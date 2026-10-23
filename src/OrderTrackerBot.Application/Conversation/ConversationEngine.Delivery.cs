using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Application.Templates;

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
                MessageTemplates.Get("delivery.001", (seller.DefaultDeliveryCharge > 0 ? Formatters.Money(seller.DefaultDeliveryCharge) : "free (0)")), ct);
            return;
        }

        seller.DefaultDeliveryCharge = amount.Value;
        await ReplyAsync(seller, amount.Value > 0
            ? MessageTemplates.Get("delivery.004", Formatters.Money(amount.Value))
            : MessageTemplates.Get("delivery.005"), ct);
    }

    private async Task HandleOrderDeliveryChargeAsync(Seller seller, SessionContextData ctx, int number, decimal amount, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, number);
        var order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.028", number), ct);
            return;
        }
        if (IsDispatched(order))
        {
            await ReplyAsync(seller, MessageTemplates.Get("delivery.002", order.Id, Formatters.Status(order.Status), order.Id), ct);
            return;
        }

        order.Total = OrderTotal(order.Subtotal, order.DiscountAmount, amount);
        order.DeliveryCharge = amount;
        await RetractEditedAutoAnswersAsync(seller, order, "delivery", amount, ct);
        await ReplyAsync(seller,
            MessageTemplates.Get("delivery.003", order.Id, order.Customer?.Name, (amount > 0 ? Formatters.Money(amount) : "free"), Formatters.Money(order.Total), (fromList ? $"\nℹ️ \"{number}\" aapki last list ka number tha." : "")), ct);
    }
}
