using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// "exchange 12 1 to L": item 1 of order 12 becomes the L (or colour) listing of the same product. The agreed price stays,
// stock moves back to the old listing and out of the new one, and the change is logged like an order edit so "undo" restores it.
public partial class ConversationEngine
{
    private static readonly Regex VariantPrefix = new(@"^(?:size|colou?r)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task HandleSizeExchangeAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var orderId = cmd.Number!.Value;
        var order = await LoadOrderForEditAsync(seller, orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-exchange.001", orderId), ct);
            return;
        }
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-exchange.002", orderId), ct);
            return;
        }

        var items = order.Items.OrderBy(i => i.Id).ToList();
        var itemNo = int.Parse(cmd.Text!);
        if (itemNo < 1 || itemNo > items.Count)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-exchange.003", itemNo, orderId, items.Count), ct);
            return;
        }

        var item = items[itemNo - 1];
        var current = item.ProductId is int productId
            ? await _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.SellerId == seller.Id, ct)
            : null;
        if (current is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-exchange.004", itemNo, orderId), ct);
            return;
        }

        var variant = VariantPrefix.Replace(cmd.Text2 ?? "", "").Trim();
        var siblings = (await _db.Products
                .Where(p => p.SellerId == seller.Id && p.IsActive && p.Name == current.Name && p.UnitType == current.UnitType && p.UnitQty == current.UnitQty)
                .ToListAsync(ct))
            .Where(p => p.Id != current.Id && (string.Equals(p.Size?.Trim(), variant, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Color?.Trim(), variant, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (siblings.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-exchange.005", variant, current.Name), ct);
            return;
        }
        if (siblings.Count > 1)
        {
            await ReplyAsync(seller, MessageTemplates.Get("order-exchange.006", variant, string.Join(", ", siblings.Select(Formatters.ProductMatchLabel))), ct);
            return;
        }

        var target = siblings[0];
        var oldName = item.ProductNameSnapshot;

        // Same logging as an order edit, so one "undo" puts the item, stock and totals back.
        var before = Snapshot(order);
        var stockBefore = StockFootprint(order);
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.OrderEdited, OrderId = order.Id,
            PayloadJson = JsonSerializer.Serialize(before)
        });

        item.ProductId = target.Id;
        item.ProductNameSnapshot = ProductVariants.SnapshotName(target);
        item.UnitCost = target.CostPrice;   // profit follows the piece that actually went out; the price stays as agreed

        await ApplyStockChangeAsync(seller, stockBefore, StockFootprint(order), ct);
        await _db.SaveChangesAsync(ct);

        await ReplyAsync(seller, MessageTemplates.Get("order-exchange.007", order.Id, itemNo, oldName, item.ProductNameSnapshot, Formatters.Money(item.UnitPrice)), ct);
        await FlushStockWarningsAsync(seller.WhatsAppPhoneNumber, ct);
    }
}
