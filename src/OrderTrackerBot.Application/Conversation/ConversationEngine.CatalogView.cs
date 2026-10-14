using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

public sealed partial class ConversationEngine
{
    private const int CatalogPageSize = 10;
    private const string NoCategoryHeading = "Bina category";

    /// <summary>A product's category split into a main category and an optional sub-category: "Rashan > Cheeni" / "Rashan / Cheeni" (more levels are kept in the sub part).</summary>
    internal static (string Main, string? Sub) SplitCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return (NoCategoryHeading, null);
        var parts = category.Split(new[] { '>', '/' }, 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? (NoCategoryHeading, null) : (parts[0], parts.Length > 1 ? parts[1] : null);
    }

    /// <summary>
    /// The catalog as a list, 10 products a page, grouped by category (and sub-category) when any product has one; sorted as added (default), newest first
    /// ("catalog latest") or A-Z ("catalog a-z"); "catalog next" / "catalog prev" / "catalog page 3" move through the pages. Plain database reads, no AI.
    /// Without any category the list is flat, like before, with a hint on how to add one.
    /// </summary>
    private async Task ShowCatalogPageAsync(Seller seller, SessionContextData ctx, List<Product> products, string? action, CancellationToken ct)
    {
        var page = Math.Max(1, ctx.CatalogPage);
        switch (action)
        {
            case null: page = 1; ctx.CatalogSort = null; break;   // a plain "catalog" starts over
            case "reset": page = 1; ctx.CatalogSort = null; break;
            case "az": ctx.CatalogSort = "az"; page = 1; break;
            case "latest": ctx.CatalogSort = "latest"; page = 1; break;
            case "next": page++; break;
            case "prev": page--; break;
            default:
                if (action.StartsWith("page:", StringComparison.Ordinal) && int.TryParse(action[5..], out var n)) page = n;
                break;
        }

        var anyCategory = products.Any(p => !string.IsNullOrWhiteSpace(p.Category));
        IOrderedEnumerable<Product> InGroup(IEnumerable<Product> items) => ctx.CatalogSort switch
        {
            "az" => items.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id),
            "latest" => items.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id),
            _ => items.OrderBy(p => p.Id)
        };

        var ordered = !anyCategory
            ? InGroup(products).Select(p => (Product: p, Main: (string?)null, Sub: (string?)null)).ToList()
            : products.Select(p => (Product: p, Cat: SplitCategory(p.Category)))
                .GroupBy(x => (x.Cat.Main.ToLowerInvariant(), (x.Cat.Sub ?? "").ToLowerInvariant()))
                .OrderBy(g => g.Key.Item1 == NoCategoryHeading.ToLowerInvariant() ? 1 : 0)
                .ThenBy(g => g.Key.Item1, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Item2 == "" ? 0 : 1).ThenBy(g => g.Key.Item2, StringComparer.Ordinal)
                .SelectMany(g => InGroup(g.Select(x => x.Product)).Select(p => (Product: p, Main: (string?)g.First().Cat.Main, Sub: g.First().Cat.Sub)))
                .ToList();

        var pages = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)CatalogPageSize));
        page = Math.Clamp(page, 1, pages);
        ctx.CatalogPage = page;

        var slice = ordered.Skip((page - 1) * CatalogPageSize).Take(CatalogPageSize).ToList();
        var lines = new List<string>();
        string? lastMain = null, lastSub = null;
        for (var i = 0; i < slice.Count; i++)
        {
            var row = slice[i];
            if (row.Main is not null && (row.Main != lastMain || row.Sub != lastSub || i == 0))
            {
                if (lines.Count > 0) lines.Add("");
                if (row.Main != lastMain || i == 0) lines.Add($"📂 *{row.Main}*");
                if (row.Sub is not null) lines.Add($"   ▫️ *{row.Sub}*");
                lastMain = row.Main; lastSub = row.Sub;
            }
            var p = row.Product;
            lines.Add($"{(page - 1) * CatalogPageSize + i + 1} {Formatters.ProductLabel(p)} - {Formatters.Money(p.Price)}" + (p.StockQty is { } q ? $" (stock {q})" : ""));
        }

        var sortName = ctx.CatalogSort switch { "az" => "A-Z", "latest" => "naya pehle", _ => "jaise add kiye" };
        var pageNote = pages > 1 ? $" — page {page}/{pages}" : "";
        var uncategorised = products.Count(p => string.IsNullOrWhiteSpace(p.Category));
        var ind = Industry(seller);
        var guide = !anyCategory
            ? $"\n\n💡 Category likhne se list category-wise nazar aati hai. Likhein: {ind.MainLine}, category {ind.Category}\n(sub-category: category {ind.Category} > naam)"
            : uncategorised > 0
                ? $"\n\n💡 {uncategorised} products ki category nahi (\"{NoCategoryHeading}\"). Likhein: {ind.MainLine}, category {ind.Category}"
                : "";
        var nav = pages > 1
            ? $"\n\n📄 Page {page}/{pages} · Order: {sortName}\nAgla: catalog next · Pichla: catalog prev · A-Z: catalog a-z · Naya pehle: catalog latest · Ek category: catalog category <naam>"
            : "";
        var text = $"🛍️ Aapka Catalog ({products.Count} products){pageNote}:\n\n{string.Join("\n", lines)}{guide}{nav}\n\n" +
                   $"Naya product add karne ke liye likhein: {ind.MainLine}\n" +
                   $"Price badalne ke liye: edit product: {ind.MainLine}";

        if (pages == 1)
        {
            await ReplyAsync(seller, text, ct);
            return;
        }

        var buttons = new List<string>();
        if (page > 1) buttons.Add("⬅️ catalog prev");
        if (page < pages) buttons.Add("➡️ catalog next");
        if (buttons.Count < 3) buttons.Add(ctx.CatalogSort == "az" ? "catalog latest" : "catalog a-z");
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, text, buttons, ct);
    }
}
