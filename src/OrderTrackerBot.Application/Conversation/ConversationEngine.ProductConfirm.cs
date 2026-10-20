using System.Text.Json;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// A product line is shown back to the seller (name, colour, price, stock) and saved only after YES.
// Off when Features:ProductConfirmation is false (unit tests keep the direct save).
public partial class ConversationEngine
{
    // One or more product lines (typed, or spoken and read by the AI) are shown back; they are saved together on YES.
    private async Task AskProductConfirmationAsync(Seller seller, SessionContextData ctx, IReadOnlyList<ProductLine> lines, CancellationToken ct)
    {
        ctx.PendingProductJson = JsonSerializer.Serialize(lines);
        var body = lines.Count == 1
            ? MessageTemplates.Get("commands.050", string.Join("\n", ProductPreviewRows(lines[0])))
            : MessageTemplates.Get("commands.052", string.Join("\n", lines.Select(l =>
                $"• {l.Name} - {Formatters.Money(l.Price)}" + (ProductPreviewDetails(l) is { Length: > 0 } d ? $" ({d})" : ""))));
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, body, new[] { "✅ Save", "❌ Cancel" }, ct);
    }

    // Every fact that will be saved is shown (cost and category included); a fact the seller did not give is left out, not shown as "-".
    private static List<string> ProductPreviewRows(ProductLine line)
    {
        var rows = new List<string> { $"• Naam: {line.Name}", $"• Price: {Formatters.Money(line.Price)}" };
        var e = line.Extras;
        if (e?.Cost is { } cost) rows.Add($"• Cost: {Formatters.Money(cost)}");
        if (e?.Category is { } category) rows.Add($"• Category: {category}");
        if (e?.Size is { } size) rows.Add($"• Size: {size}");
        if (e?.Color is { } color) rows.Add($"• Rang: {color}");
        if (e?.Stock is { } stock) rows.Add($"• Stock: {stock}");
        return rows;
    }

    private static string ProductPreviewDetails(ProductLine line)
    {
        var e = line.Extras;
        var parts = new List<string>();
        if (e?.Cost is { } cost) parts.Add($"cost {Formatters.Money(cost)}");
        if (e?.Category is { } category) parts.Add($"category: {category}");
        if (e?.Size is { } size) parts.Add($"size: {size}");
        if (e?.Color is { } color) parts.Add($"rang: {color}");
        if (e?.Stock is { } stock) parts.Add($"stock: {stock}");
        return string.Join(", ", parts);
    }

    // Runs before any other handling while products wait: YES saves them, NO drops them, any other message drops them and runs as usual.
    private async Task<bool> TryHandlePendingProductAsync(Seller seller, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.PendingProductJson is null) return false;
        var pending = JsonSerializer.Deserialize<List<ProductLine>>(ctx.PendingProductJson);
        ctx.PendingProductJson = null;
        if (pending is null || pending.Count == 0) return false;
        if (CommandParser.IsAffirmative(message))
        {
            foreach (var line in pending)
                await HandleAddProductAsync(seller, new ParsedCommand { Kind = CommandKind.AddProduct, Text = line.Name, Amount = line.Price, Product = line }, ct);
            return true;
        }
        if (CommandParser.IsNegative(message))
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.051"), ct);
            return true;
        }
        // A correction to the waiting product ("category is Summer Wear", "variants Excel mein hain") is applied and shown again,
        // not read as a new product. Anything else still drops the waiting product, as before.
        if (pending.Count == 1 && ProductCorrectionOf(message) is { } correction)
        {
            var line = pending[0];
            if (correction.Category is not null)
                line = line with { Extras = (line.Extras ?? new ProductExtras(null, null, null, null, null, null, null)) with { Category = correction.Category } };
            await AskProductConfirmationAsync(seller, ctx, new[] { line }, ct);
            if (correction.VariantsInSheet) await ReplyAsync(seller, MessageTemplates.Get("commands.054"), ct);
            return true;
        }
        return false;
    }

    // Context for the AI while a product waits for YES: a spoken correction to it (category, variants, price, stock) must come back as a
    // plain step for that product, so the model does not read the same product again as a new one. Null when none waits.
    internal static string? PendingProductSituation(SessionContextData ctx)
    {
        if (ctx.PendingProductJson is null) return null;
        var waiting = JsonSerializer.Deserialize<List<ProductLine>>(ctx.PendingProductJson);
        if (waiting is not { Count: > 0 }) return null;
        var described = string.Join("; ", waiting.Select(l =>
        {
            var e = l.Extras;
            var parts = new List<string> { $"name \"{l.Name}\"", $"price {l.Price}" };
            if (e?.Cost is { } cost) parts.Add($"cost {cost}");
            if (e?.Stock is { } stock) parts.Add($"stock {stock}");
            if (e?.Category is { } category) parts.Add($"category \"{category}\"");
            if (e?.Size is { } size) parts.Add($"size \"{size}\"");
            if (e?.Color is { } color) parts.Add($"colour \"{color}\"");
            return string.Join(", ", parts);
        }));
        return $"A product is waiting for the seller's YES and is NOT saved yet: {described}. " +
               "If the seller now corrects it, reply with one plain step for that same product, e.g. \"category Summer Wear\" or \"variants excel mein hain\" (variants in a sheet/Excel file), " +
               "or the new price/stock as \"<name> - <price>\". Never list that product again as a new product.";
    }

    // The category and/or "variants are in a sheet" from one message; null when the message is neither.
    private static (string? Category, bool VariantsInSheet)? ProductCorrectionOf(string message)
    {
        var category = CommandParser.ProductCategory.Match(message) is { Success: true } m
            ? System.Text.RegularExpressions.Regex.Replace(m.Groups["c"].Value.Trim(), @"\s+(?:hai|hain|ہے|ہیں)$", "")
            : null;
        var variants = VariantsInFile.IsMatch(message);
        return category is null && !variants ? null : (category, variants);
    }
}
