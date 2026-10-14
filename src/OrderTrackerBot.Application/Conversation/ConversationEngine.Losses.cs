using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Losses and wages (owner phone): "loss 2 Kurti damaged" writes stock off and is valued at the product's cost; "Ali ki 5000 dihari"
// logs a worker's pay. Both are undoable like expenses. "monthly net" subtracts wages and shows losses at cost on their own line.
public partial class ConversationEngine
{
    private const decimal MaxWageAmount = 100_000_000m;
    private const int MaxLossQuantity = 100_000;

    private async Task<List<Loss>> LoadLossesAsync(Seller seller, DateTime start, DateTime end, CancellationToken ct) =>
        await _db.Losses.AsNoTracking().Where(x => x.SellerId == seller.Id && x.CreatedAt >= start && x.CreatedAt < end).ToListAsync(ct);

    private async Task<List<WageEntry>> LoadWagesAsync(Seller seller, DateTime start, DateTime end, CancellationToken ct) =>
        await _db.WageEntries.AsNoTracking().Where(x => x.SellerId == seller.Id && x.CreatedAt >= start && x.CreatedAt < end).ToListAsync(ct);

    private async Task HandleLossAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var quantity = (int)(cmd.Amount ?? 0);
        var name = cmd.Text?.Trim() ?? "";
        if (quantity <= 0 || quantity > MaxLossQuantity || name.Length == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("losses.001"), ct);
            return;
        }

        var product = await FindProductAsync(seller, name, ct);
        var loss = new Loss
        {
            SellerId = seller.Id, ProductId = product?.Id, ProductName = product is null ? name : Formatters.ProductLabel(product),
            Quantity = quantity, UnitCost = product?.CostPrice ?? 0, Reason = cmd.Text2
        };
        _db.Losses.Add(loss);
        if (product is not null) await ApplyStockChangeAsync(seller, new Dictionary<int, int>(), new Dictionary<int, int> { [product.Id] = quantity }, ct);
        await _db.SaveChangesAsync(ct);
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.LossLogged,
            PayloadJson = JsonSerializer.Serialize(new { LossId = loss.Id, ProductId = product?.Id, loss.Quantity })
        });

        var notes = new List<string>();
        if (product is null) notes.Add($"\"{name}\" catalog mein nahi — cost 0 se likha.");
        else if (product.CostPrice is null) notes.Add("Is product ki cost price nahi hai — nuqsan 0 cost se likha.");
        if (product?.StockQty is { } left) notes.Add($"Stock ab: {left}");
        await ReplyAsync(seller,
            MessageTemplates.Get("losses.002", quantity, loss.ProductName, (cmd.Text2 is null ? "" : $" ({cmd.Text2})"), Formatters.Money(loss.UnitCost * quantity), (notes.Count == 0 ? "" : "\n" + string.Join("\n", notes))), ct);
    }

    private async Task UndoLossAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(log.PayloadJson);
        var id = doc.RootElement.GetProperty("LossId").GetInt32();
        var loss = await _db.Losses.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == seller.Id, ct);
        if (loss is null) return;
        _db.Losses.Remove(loss);
        if (loss.ProductId is { } productId)
            await ApplyStockChangeAsync(seller, new Dictionary<int, int> { [productId] = loss.Quantity }, new Dictionary<int, int>(), ct);
        await ReplyAsync(seller, MessageTemplates.Get("losses.003", loss.Quantity, loss.ProductName), ct);
    }

    private async Task HandleWageAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var amount = cmd.Amount ?? 0;
        var worker = cmd.Text?.Trim() ?? "";
        if (worker.Length == 0 || amount <= 0 || amount > MaxWageAmount)
        {
            await ReplyAsync(seller, MessageTemplates.Get("losses.004"), ct);
            return;
        }

        var wage = new WageEntry { SellerId = seller.Id, WorkerName = worker, Amount = amount, Days = cmd.Number };
        _db.WageEntries.Add(wage);
        await _db.SaveChangesAsync(ct);
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.WageLogged,
            PayloadJson = JsonSerializer.Serialize(new { WageId = wage.Id })
        });

        var (start, end, _) = ExpensePeriod(seller, "month");
        var monthTotal = (await LoadWagesAsync(seller, start, end, ct)).Sum(x => x.Amount);
        var daysText = wage.Days is { } d ? $" ({d} din)" : "";
        await ReplyAsync(seller,
            MessageTemplates.Get("losses.005", worker, Formatters.Money(amount), daysText, Formatters.Money(monthTotal)), ct);
    }

    private async Task UndoWageAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(log.PayloadJson);
        var id = doc.RootElement.GetProperty("WageId").GetInt32();
        var wage = await _db.WageEntries.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == seller.Id, ct);
        if (wage is null) return;
        _db.WageEntries.Remove(wage);
        await ReplyAsync(seller, MessageTemplates.Get("losses.006", wage.WorkerName, Formatters.Money(wage.Amount)), ct);
    }
}
