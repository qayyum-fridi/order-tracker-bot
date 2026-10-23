using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Lookups by what a seller or buyer types: orders ("search order Sara", "search order 12") and products by their design code or SKU in free text.
public partial class ConversationEngine
{
    private static readonly Regex NameParts = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant);
    private static readonly string[] CodeAttributeKeys = { "design", "design code", "design no", "code", "sku", "item code" };

    /// <summary>
    /// The codes a product can be typed as, lower-cased: its barcode / SKU, the number-bearing parts of its name ("Design 1203" gives "1203", "KRT-001" gives "001"),
    /// and a design / code / sku attribute ("design: 1203").
    /// </summary>
    internal static IReadOnlyList<string> ProductCodes(Product product)
    {
        var codes = new List<string>();
        if (!string.IsNullOrWhiteSpace(product.Sku)) codes.Add(product.Sku.Trim().ToLowerInvariant());
        foreach (var part in NameParts.Split(product.Name))
            if (part.Length >= 3 && part.Any(char.IsDigit)) codes.Add(part.ToLowerInvariant());
        var attributes = ProductAttributes(product);
        foreach (var key in CodeAttributeKeys)
            if (attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) codes.Add(value.Trim().ToLowerInvariant());
        return codes;
    }

    /// <summary>True when the typed text (already lower-cased) is one of the product's codes.</summary>
    internal static bool HasProductCode(Product product, string wantedLower) =>
        wantedLower.Length > 0 && ProductCodes(product).Contains(wantedLower);

    /// <summary>
    /// Orders matching the words: the order number ("12" or "#12"), a customer's name or phone, a product name on the order, or a product's design code / SKU.
    /// Newest first, a page at a time like the catalog ("orders next" / "orders prev" move through it), so "order 12" opens one. Plain database reads, no AI.
    /// </summary>
    private async Task HandleOrderSearchAsync(Seller seller, SessionContextData ctx, string query, CancellationToken ct)
    {
        var wanted = query.Trim().TrimStart('#').ToLowerInvariant();
        var orderId = int.TryParse(wanted, out var number) ? number : -1;
        var codeProductIds = (await _db.Products.Where(p => p.SellerId == seller.Id).ToListAsync(ct))
            .Where(p => HasProductCode(p, wanted)).Select(p => p.Id).ToList();

        var matches = _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && (
                o.Id == orderId
                || (o.Customer != null && (o.Customer.Name.ToLower().Contains(wanted) || (o.Customer.Phone ?? "").Contains(wanted)))
                || o.Items.Any(i => i.ProductNameSnapshot.ToLower().Contains(wanted) || (i.ProductId != null && codeProductIds.Contains(i.ProductId.Value)))));

        var total = await matches.CountAsync(ct);
        if (total == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("search.001", query), ct);
            return;
        }

        var paging = OrderPageFor(ctx, CommandKind.OrderSearch, query, total);
        var found = await matches.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Skip(paging.Skip).Take(paging.Take).ToListAsync(ct);
        var lines = found.Select(o =>
            $"#{o.Id} {o.Customer?.Name} - {Formatters.ItemsSummary(o)} - {Formatters.Money(o.Total)} - {Formatters.Status(seller.PreferredLanguage, o.Status)}");
        await ReplyOrderPageAsync(seller, MessageTemplates.Get("search.002", total, query, string.Join("\n", lines), ""), paging, ct);
    }
}
