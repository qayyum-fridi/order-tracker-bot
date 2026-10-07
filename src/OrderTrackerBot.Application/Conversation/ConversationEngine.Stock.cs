using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Stock: products with a StockQty are tracked; untracked (null) ones are never touched. Every active order "holds" its items'
// quantities and a cancelled/returned one holds nothing, so each change (save, cancel, return, edit, undo) only applies the
// difference between the order's footprint before and after — stock can't be counted twice.
public partial class ConversationEngine
{
    private const int LowStockThreshold = 3;
    private readonly List<string> _stockWarnings = new();

    private static Dictionary<int, int> StockFootprint(Order order) =>
        order.Status is OrderStatus.Cancelled or OrderStatus.Returned
            ? new Dictionary<int, int>()
            : order.Items.Where(i => i.ProductId is not null).GroupBy(i => i.ProductId!.Value).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

    private async Task ApplyStockChangeAsync(Seller seller, Dictionary<int, int> before, Dictionary<int, int> after, CancellationToken ct)
    {
        foreach (var productId in before.Keys.Union(after.Keys))
        {
            var delta = after.GetValueOrDefault(productId) - before.GetValueOrDefault(productId);
            if (delta == 0) continue;
            var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.SellerId == seller.Id, ct);
            if (product?.StockQty is null) continue;

            product.StockQty -= delta;
            if (delta > 0 && product.StockQty <= LowStockThreshold)
                _stockWarnings.Add(product.StockQty <= 0
                    ? $"⛔ {Formatters.ProductLabel(product)}: stock khatam ({product.StockQty})"
                    : $"⚠️ {Formatters.ProductLabel(product)}: sirf {product.StockQty} bache");
        }
    }

    private async Task FlushStockWarningsAsync(string phone, CancellationToken ct)
    {
        if (_stockWarnings.Count == 0) return;
        var text = "📉 Stock alert:\n" + string.Join("\n", _stockWarnings.Distinct()) + "\n\nNaya stock add karne ke liye: \"stock Kurti +10\"";
        _stockWarnings.Clear();
        await _sender.SendTextMessageAsync(phone, text, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task HandleStockAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Id).ToListAsync(ct);
        if (cmd.Text is null)
        {
            var tracked = products.Where(p => p.StockQty is not null).ToList();
            if (tracked.Count == 0)
            {
                await ReplyAsync(seller,
                    "📦 Abhi kisi product ka stock track nahi ho raha.\n\nShuru karne ke liye: \"stock Kurti 20\"\n" +
                    "Phir har order par stock khud kam hoga, cancel/return par wapas aayega, aur kam hone par alert milega.", ct);
                return;
            }
            var lines = tracked.Select(p => $"{(p.StockQty <= 0 ? "⛔" : p.StockQty <= LowStockThreshold ? "⚠️" : "✅")} {Formatters.ProductLabel(p)}: {p.StockQty}");
            await ReplyAsync(seller,
                $"📦 Stock ({tracked.Count}):\n{string.Join("\n", lines)}\n\n" +
                "Set: \"stock Kurti 20\" · Add: \"stock Kurti +10\" · Band: \"stock Kurti off\"", ct);
            return;
        }

        var matches = products.Where(p => string.Equals(p.Name, cmd.Text, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(Formatters.ProductLabel(p), cmd.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            matches = products.Where(p => p.Name.Contains(cmd.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
        {
            await ReplyAsync(seller, matches.Count == 0
                ? $"\"{cmd.Text}\" catalog mein nahi mila."
                : $"\"{cmd.Text}\" se kai products milte hain: {string.Join(", ", matches.Select(Formatters.ProductLabel))} — poora naam likhein.", ct);
            return;
        }

        var product = matches[0];
        switch (cmd.Text2)
        {
            case "off":
                product.StockQty = null;
                await ReplyAsync(seller, $"✅ {Formatters.ProductLabel(product)} ka stock ab track nahi hoga.", ct);
                return;
            case "add":
                product.StockQty = (product.StockQty ?? 0) + (int)cmd.Amount!.Value;
                break;
            default:
                product.StockQty = (int)cmd.Amount!.Value;
                break;
        }
        await ReplyAsync(seller, $"✅ {Formatters.ProductLabel(product)} — stock: {product.StockQty}. Har order par khud kam hoga.", ct);
    }
}
