using System.Globalization;
using Microsoft.EntityFrameworkCore;
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
    private const string AutoSkuPrefix = "AUTO-";

    /// <summary>A block copied from a web page or a list (several lines, or long): its products are shown for YES like a screenshot.</summary>
    internal static bool IsPastedProductBlock(string message) => message.Count(c => c == '\n') >= 2 || message.Length > 250;

    /// <summary>The next code this shop gives a product without its own SKU: AUTO-00001, AUTO-00002 ...</summary>
    private async Task<string> NextAutoSkuAsync(Seller seller, CancellationToken ct)
    {
        var used = await _db.Products.Where(p => p.SellerId == seller.Id && p.Sku != null && p.Sku.StartsWith(AutoSkuPrefix)).Select(p => p.Sku!).ToListAsync(ct);
        var highest = used.Select(s => int.TryParse(s[AutoSkuPrefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return $"{AutoSkuPrefix}{highest + 1:D5}";
    }

    private async Task ProposeScreenshotProductsAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, CancellationToken ct)
    {
        if (action.Products.Any(p => string.IsNullOrWhiteSpace(p.Name)))
        {
            await ReplyAsync(seller, MessageTemplates.Get("picture-product.005"), ct);
            return;
        }
        var withoutPrice = action.Products.FirstOrDefault(p => p.Price is null);
        if (withoutPrice is not null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("picture-product.002", withoutPrice.Name), ct);
            return;
        }
        // One listing per standard size (same name, colour and price; size is what tells them apart).
        var lines = action.Products.SelectMany(p => p.Sizes.Count == 0
            ? new[] { ScreenshotProductLine(p) }
            : p.Sizes.Select(size => ScreenshotProductLine(p, size))).ToList();

        // A category the seller has not created yet is asked about in the same confirmation; YES creates it, then saves the products.
        var newCategories = new List<string>();
        foreach (var category in action.Products.Select(p => p.Attributes.TryGetValue("category", out var c) ? c.Trim() : "")
                     .Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!await CategoryKnownAsync(seller, category, ct)) newCategories.Add(category);

        var question = newCategories.Count == 0 ? "" : MessageTemplates.Get("picture-product.006", string.Join(", ", newCategories)) + "\n\n";
        ctx.ModelEdit = new ModelEditData { Kind = PictureProductKind, Lines = lines, Field = string.Join("|", newCategories) };
        SetState(session, ConversationState.AwaitingModelStatusConfirmation);
        await AskYesNoAsync(seller, question + MessageTemplates.Get("picture-product.001", string.Join("\n", lines)), ct);
    }

    /// <summary>One typed product line: "Name - price, cost X, stock N, color C, size S, sku K, category G, fabric: F, ...".</summary>
    internal static string ScreenshotProductLine(AiNewProduct product, string? size = null)
    {
        var parts = new List<string>();
        if (product.Cost is { } cost) parts.Add($"cost {cost.ToString("0.##", CultureInfo.InvariantCulture)}");
        // The stock on the page is for all sizes together, so it is not put on one size's listing.
        if (product.Stock is { } stock && size is null) parts.Add($"stock {stock}");
        if (size is not null) parts.Add($"size {size}");
        if (product.Colors.Count > 0) parts.Add($"color {string.Join(" / ", product.Colors)}");
        foreach (var (key, value) in product.Attributes)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var name = key.Trim().ToLowerInvariant();
            // The site's own SKU is not copied: the shop gives its own code.
            if (name is "price" or "stock" or "cost" or "sku" || (name == "color" && product.Colors.Count > 0)) continue;
            if (name == "size" && (size is not null || product.Sizes.Count > 0)) continue;
            parts.Add(name switch
            {
                "colour" => $"color {value.Trim()}",
                "size" or "category" or "sku" => $"{name} {value.Trim()}",
                _ => $"{key.Trim()}: {value.Trim()}",
            });
        }
        // " - " inside the name would split the price off the name when the line is read back: a space keeps the name whole.
        var name = string.Join(" ", product.Name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace(" - ", " ");
        // The price is the current one. If the page shows a crossed-out higher price, the lower one is saved and the old one is kept as a detail.
        var price = product.Price!.Value;
        var current = product.OldPrice is { } other ? Math.Min(price, other) : price;
        var crossedOut = product.OldPrice is { } other2 ? Math.Max(price, other2) : (decimal?)null;
        var head = $"{name} - {current.ToString("0.##", CultureInfo.InvariantCulture)}";
        if (crossedOut is { } old && old > current) parts.Insert(0, $"old price: {old.ToString("0.##", CultureInfo.InvariantCulture)}");
        return string.Join(", ", new[] { head }.Concat(parts));
    }

    private async Task SaveScreenshotProductsAsync(Seller seller, SessionContextData ctx, IReadOnlyList<string> lines, string? newCategories, CancellationToken ct)
    {
        foreach (var category in (newCategories ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
            await HandleCreateCategoryAsync(seller, category, ct);
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
