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

    private const int DefaultCatalogPageSize = 10, MinCatalogPageSize = 5, MaxCatalogPageSize = 50;

    private static bool CatalogMatches(Product p, string[] words)
    {
        var haystack = string.Join(" ", new[] { p.Name, p.Category, p.Sku, p.Color, p.Size, p.AttributesJson }.Where(v => !string.IsNullOrWhiteSpace(v))).ToLowerInvariant();
        return words.All(w => haystack.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// The catalog as a list: a page at a time (10 by default, the seller can choose 5-50 and it is remembered), grouped by category (and sub-category) when any
    /// product has one, sorted as added (default), newest first ("catalog latest") or A-Z ("catalog a-z"). "catalog next / prev / last / page 8" move through the
    /// pages, "catalog search chini" shows only the matches (name, category, code, colour, size, other details) in the same paged list until "catalog reset".
    /// "catalog size 25" sets the page size. Plain database reads, no AI. Without any category the list is flat, with a hint on how to add one.
    /// </summary>
    private async Task ShowCatalogPageAsync(Seller seller, SessionContextData ctx, List<Product> allProducts, string? action, CancellationToken ct)
    {
        var page = Math.Max(1, ctx.CatalogPage);
        string? notice = null;
        switch (action)
        {
            case null: page = 1; ctx.CatalogSort = null; ctx.CatalogQuery = null; break;   // a plain "catalog" starts over (the page size stays)
            case "reset": page = 1; ctx.CatalogSort = null; ctx.CatalogQuery = null; break;
            case "az": ctx.CatalogSort = "az"; page = 1; break;
            case "latest": ctx.CatalogSort = "latest"; page = 1; break;
            case "next": page++; break;
            case "prev": page--; break;
            case "last": page = int.MaxValue; break;
            default:
                if (action.StartsWith("page:", StringComparison.Ordinal) && int.TryParse(action[5..], out var n)) page = n;
                else if (action.StartsWith("size:", StringComparison.Ordinal) && int.TryParse(action[5..], out var size))
                {
                    var clamped = Math.Clamp(size, MinCatalogPageSize, MaxCatalogPageSize);
                    ctx.CatalogPageSize = clamped;
                    page = 1;
                    notice = clamped == size
                        ? $"✅ Ab har page par {clamped} products dikhenge (yeh yaad rahega)."
                        : $"ℹ️ Ek page par {MinCatalogPageSize} se {MaxCatalogPageSize} tak products ho sakte hain: {clamped} set kar diya (yeh yaad rahega).";
                }
                else if (action.StartsWith("q:", StringComparison.Ordinal))
                {
                    ctx.CatalogQuery = action[2..].Trim();
                    page = 1;
                }
                break;
        }

        var pageSize = ctx.CatalogPageSize is >= MinCatalogPageSize and <= MaxCatalogPageSize ? ctx.CatalogPageSize : DefaultCatalogPageSize;
        var products = allProducts;
        if (!string.IsNullOrWhiteSpace(ctx.CatalogQuery))
        {
            var words = ctx.CatalogQuery.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            products = allProducts.Where(p => CatalogMatches(p, words)).ToList();
            if (products.Count == 0)
            {
                var query = ctx.CatalogQuery;
                var near = TextDistance.Nearest(query, allProducts.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase), 2, 1);
                ctx.CatalogQuery = null;
                await ReplyAsync(seller,
                    $"🔎 \"{query}\": koi product nahi mila ({allProducts.Count} products mein se)." +
                    (near.Count > 0 ? $"\n💡 Kya aap \"{near[0]}\" keh rahe hain? Likhein: catalog search {near[0]}" : "") +
                    "\nPoori list: catalog · Dobara talash: catalog search <naam>", ct);
                return;
            }
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

        var pages = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)pageSize));
        page = Math.Clamp(page, 1, pages);
        ctx.CatalogPage = page;

        var first = (page - 1) * pageSize;
        var slice = ordered.Skip(first).Take(pageSize).ToList();
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
            lines.Add($"{first + i + 1} {Formatters.ProductLabel(p)} - {Formatters.Money(p.Price)}" + (p.StockQty is { } q ? $" (stock {q})" : ""));
        }

        var sortName = ctx.CatalogSort switch { "az" => "A-Z", "latest" => "naya pehle", _ => "jaise add kiye" };
        var searching = !string.IsNullOrWhiteSpace(ctx.CatalogQuery);
        var title = searching
            ? $"🔎 Talash: \"{ctx.CatalogQuery}\" — {products.Count} nateeje ({allProducts.Count} mein se)"
            : $"🛍️ Aapka Catalog ({products.Count} products)";
        var range = pages > 1 ? $"\nDikh rahe hain {first + 1}–{first + slice.Count} / {products.Count} · Page {page}/{pages} · Order: {sortName}" : "";
        var uncategorised = products.Count(p => string.IsNullOrWhiteSpace(p.Category));
        var ind = Industry(seller);
        var guide = searching ? "" : !anyCategory
            ? $"\n\n💡 Category likhne se list category-wise nazar aati hai. Likhein: {ind.MainLine}, category {ind.Category}\n(sub-category: category {ind.Category} > naam)"
            : uncategorised > 0
                ? $"\n\n💡 {uncategorised} products ki category nahi (\"{NoCategoryHeading}\"). Likhein: {ind.MainLine}, category {ind.Category}"
                : "";
        var nav = pages > 1
            ? $"\n\n📄 Agla: catalog next · Pichla: catalog prev · Seedha page: catalog page {Math.Min(pages, page + 1)} · Aakhri: catalog last\n" +
              $"🔤 A-Z: catalog a-z · Naya pehle: catalog latest · Ek page par {pageSize}: catalog size 25 (5–{MaxCatalogPageSize})\n" +
              "🔎 Talash: catalog search <naam / category / code>" + (searching ? " · Talash band: catalog reset" : "")
            : (allProducts.Count > DefaultCatalogPageSize || searching ? "\n\n🔎 Talash: catalog search <naam> · Poori list: catalog reset" : "");
        var text = (notice is null ? "" : notice + "\n\n") + $"{title}:{range}\n\n{string.Join("\n", lines)}{guide}{nav}" + (searching ? "" :
                   $"\n\nNaya product add karne ke liye likhein: {ind.MainLine}\n" +
                   $"Price badalne ke liye: edit product: {ind.MainLine}");

        if (pages == 1)
        {
            await ReplyAsync(seller, text, ct);
            return;
        }

        var buttons = new List<string>();
        if (page > 1) buttons.Add("⬅️ catalog prev");
        if (page < pages) buttons.Add("➡️ catalog next");
        if (buttons.Count < 3) buttons.Add(searching && buttons.Count == 1 ? "catalog reset" : ctx.CatalogSort == "az" ? "catalog latest" : "catalog a-z");
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, text, buttons, ct);
    }
}
