using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Counter return: "return Shirt 1 cash" = a buyer brings one or more pieces back with no order. The product must be in the catalog (its price is the refund).
// Stock goes back at once. A cash refund with an open drawer is a cash-out movement; with the drawer closed nothing is moved and the reply says so.
// One ActionLog row lets "undo" take all of it back. Sales reports do not subtract counter returns yet (see the PR notes).
public partial class ConversationEngine
{
    private async Task HandleCounterReturnAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var qty = cmd.Number ?? 0;
        if (qty <= 0 || qty > 999 || string.IsNullOrWhiteSpace(cmd.Text))
        {
            await ReplyAsync(seller, MessageTemplates.Get("counter-return.004"), ct);
            return;
        }

        var catalog = await LoadCatalogAsync(seller, ct);
        if (FindCatalogEntry(catalog, cmd.Text!) is not { } entry)
        {
            await ReplyAsync(seller, MessageTemplates.Get("counter-return.001", cmd.Text), ct);
            return;
        }

        var product = entry.Product;
        var refund = cmd.Text2 is "cash" or "bank" ? cmd.Text2 : "none";
        var amount = product.Price * qty;

        int? movementId = null;
        var drawerOpen = false;
        if (refund == "cash")
        {
            var drawer = await _db.CashDrawerSessions.FirstOrDefaultAsync(s => s.SellerId == seller.Id && s.ClosedAt == null, ct);
            if (drawer is not null)
            {
                drawerOpen = true;
                var movement = new CashMovement { SellerId = seller.Id, SessionId = drawer.Id, Kind = CashMovementKind.Out, Amount = amount, Note = $"return: {product.Name} x{qty}", CreatedAt = DateTime.UtcNow };
                _db.CashMovements.Add(movement);
                await _db.SaveChangesAsync(ct);
                movementId = movement.Id;
            }
        }

        var stockBefore = product.StockQty;
        if (stockBefore is { } tracked) product.StockQty = tracked + qty;

        var record = new CounterReturn
        {
            SellerId = seller.Id, ProductId = product.Id, ProductName = product.Name, Quantity = qty,
            Amount = amount, RefundMethod = refund, CashMovementId = movementId, CreatedAt = DateTime.UtcNow
        };
        _db.CounterReturns.Add(record);
        await _db.SaveChangesAsync(ct);
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.CounterReturned,
            PayloadJson = JsonSerializer.Serialize(new { CounterReturnId = record.Id })
        });
        await _db.SaveChangesAsync(ct);

        var stockLine = product.StockQty is { } now ? MessageTemplates.Get("counter-return.006", now) : "";
        var drawerLine = refund == "cash" && !drawerOpen ? MessageTemplates.Get("counter-return.003") : "";
        await ReplyAsync(seller,
            MessageTemplates.Get("counter-return.002", product.Name, qty, Formatters.Money(amount), MessageTemplates.Get("counter-return.refund." + refund), stockLine + drawerLine), ct);
    }

    /// <summary>Undo of a counter return: the record goes, stock is taken back out and the cash-out movement is removed.</summary>
    private async Task UndoCounterReturnAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(log.PayloadJson);
        var id = doc.RootElement.GetProperty("CounterReturnId").GetInt32();
        var record = await _db.CounterReturns.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == seller.Id, ct);
        if (record is null) return;

        var product = record.ProductId is { } productId
            ? await _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.SellerId == seller.Id, ct)
            : null;
        if (product?.StockQty is { } tracked) product.StockQty = tracked - record.Quantity;
        if (record.CashMovementId is { } movementId)
        {
            var movement = await _db.CashMovements.FirstOrDefaultAsync(m => m.Id == movementId && m.SellerId == seller.Id, ct);
            if (movement is not null) _db.CashMovements.Remove(movement);
        }
        _db.CounterReturns.Remove(record);
        await _db.SaveChangesAsync(ct);
        await ReplyAsync(seller, MessageTemplates.Get("counter-return.005", record.ProductName, record.Quantity), ct);
    }
}
