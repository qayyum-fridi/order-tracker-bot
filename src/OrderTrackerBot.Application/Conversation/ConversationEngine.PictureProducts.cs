using System.Globalization;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// A product screenshot: every detail the picture shows (name, price, cost, stock, colour, size, SKU, category, and the
// other fields such as fabric or pieces) is shown as one product line per product, which the seller can correct and resend.
// Nothing is saved until the seller says YES. A corrected line sent instead is handled as a typed product line.
public partial class ConversationEngine
{
    internal const string PictureProductKind = "add_picture_product";

    private async Task ProposeScreenshotProductsAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, CancellationToken ct)
    {
        var withoutPrice = action.Products.FirstOrDefault(p => p.Price is null);
        if (withoutPrice is not null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("picture-product.002", withoutPrice.Name), ct);
            return;
        }
        var lines = action.Products.Select(ScreenshotProductLine).ToList();
        ctx.ModelEdit = new ModelEditData { Kind = PictureProductKind, Lines = lines };
        SetState(session, ConversationState.AwaitingModelStatusConfirmation);
        await AskYesNoAsync(seller, MessageTemplates.Get("picture-product.001", string.Join("\n", lines)), ct);
    }

    /// <summary>One typed product line: "Name - price, cost X, stock N, color C, size S, sku K, category G, fabric: F, ...".</summary>
    internal static string ScreenshotProductLine(AiNewProduct product)
    {
        var parts = new List<string>();
        if (product.Cost is { } cost) parts.Add($"cost {cost.ToString("0.##", CultureInfo.InvariantCulture)}");
        if (product.Stock is { } stock) parts.Add($"stock {stock}");
        if (product.Colors.Count > 0) parts.Add($"color {string.Join(" / ", product.Colors)}");
        foreach (var (key, value) in product.Attributes)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var name = key.Trim().ToLowerInvariant();
            if (name is "price" or "stock" or "cost" || (name == "color" && product.Colors.Count > 0)) continue;
            parts.Add(name switch
            {
                "colour" => $"color {value.Trim()}",
                "size" or "category" or "sku" => $"{name} {value.Trim()}",
                _ => $"{key.Trim()}: {value.Trim()}",
            });
        }
        var head = $"{product.Name.Trim()} - {product.Price!.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
        return string.Join(", ", new[] { head }.Concat(parts));
    }

    private async Task SaveScreenshotProductsAsync(Seller seller, SessionContextData ctx, IReadOnlyList<string> lines, CancellationToken ct)
    {
        foreach (var line in lines)
        {
            var parsed = CommandParser.TryParse(line);
            if (parsed?.Kind != CommandKind.AddProduct)
            {
                await ReplyAsync(seller, MessageTemplates.Get("picture-product.004", line), ct);
                continue;
            }
            await HandleAddProductAsync(seller, parsed, ct, ctx);
        }
    }
}
