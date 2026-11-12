using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Business-level categories: the seller's list lives in Categories (a parent and one sub-category level).
// A product keeps the text "Parent > Child" in Product.Category, so the catalog view and reports read it as before.
// A product line that names a category the seller has not created is refused with the way to create it.
public partial class ConversationEngine
{
    private const int MaxCategoryNameLength = 40;

    /// <summary>"Winter 26 > Sukoon" gives ("Winter 26", "Sukoon"); "Shirts" gives ("Shirts", null). Null when empty, too deep or too long.</summary>
    internal static (string Parent, string? Child)? SplitCategoryPath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(new[] { '>', '/' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(p => p.Length > MaxCategoryNameLength)) return null;
        return (parts[0], parts.Length == 2 ? parts[1] : null);
    }

    internal static string CategoryPathText(string parent, string? child) => child is null ? parent : $"{parent} > {child}";

    /// <summary>True when the category exists for this seller: created in the list, or already carried by one of their products (rows from before this table).</summary>
    private async Task<bool> CategoryKnownAsync(Seller seller, string text, CancellationToken ct)
    {
        var path = SplitCategoryPath(text);
        if (path is null) return false;
        var (parent, child) = path.Value;
        var rows = await _db.Categories.Where(c => c.SellerId == seller.Id).ToListAsync(ct);
        var root = rows.FirstOrDefault(c => c.ParentId is null && string.Equals(c.Name, parent, StringComparison.OrdinalIgnoreCase));
        var inList = child is null
            ? root is not null
            : root is not null && rows.Any(c => c.ParentId == root.Id && string.Equals(c.Name, child, StringComparison.OrdinalIgnoreCase));
        if (inList) return true;
        var lower = CategoryPathText(parent, child).ToLowerInvariant();
        return await _db.Products.AnyAsync(p => p.SellerId == seller.Id && p.Category != null && p.Category.ToLower() == lower, ct);
    }

    /// <summary>The categories the seller can use: the list, then any product text not in it, as "Parent > Child".</summary>
    private async Task<string> CategoryListTextAsync(Seller seller, CancellationToken ct)
    {
        var rows = await _db.Categories.Where(c => c.SellerId == seller.Id).ToListAsync(ct);
        var names = new List<string>();
        foreach (var root in rows.Where(c => c.ParentId is null).OrderBy(c => c.Name))
        {
            names.Add(root.Name);
            names.AddRange(rows.Where(c => c.ParentId == root.Id).OrderBy(c => c.Name).Select(c => CategoryPathText(root.Name, c.Name)));
        }
        var fromProducts = await _db.Products.Where(p => p.SellerId == seller.Id && p.Category != null).Select(p => p.Category!).Distinct().ToListAsync(ct);
        foreach (var text in fromProducts.OrderBy(t => t))
            if (!names.Any(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase))) names.Add(text);
        return names.Count == 0 ? MessageTemplates.Get("category.002") : string.Join(", ", names);
    }

    /// <summary>"Ready to Wear banao" / "Winter 26 > Sukoon banao": adds the category (and its parent when new). Nothing else is changed.</summary>
    private async Task HandleCreateCategoryAsync(Seller seller, string text, CancellationToken ct)
    {
        var path = SplitCategoryPath(text);
        if (path is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("category.005"), ct);
            return;
        }
        var (parent, child) = path.Value;
        var rows = await _db.Categories.Where(c => c.SellerId == seller.Id).ToListAsync(ct);
        var root = rows.FirstOrDefault(c => c.ParentId is null && string.Equals(c.Name, parent, StringComparison.OrdinalIgnoreCase));
        var label = CategoryPathText(parent, child);

        if (child is null)
        {
            if (root is not null)
            {
                await ReplyAsync(seller, MessageTemplates.Get("category.003", label), ct);
                return;
            }
            _db.Categories.Add(new Category { SellerId = seller.Id, Name = parent });
        }
        else
        {
            if (root is null)
            {
                root = new Category { SellerId = seller.Id, Name = parent };
                _db.Categories.Add(root);
            }
            if (rows.Any(c => c.ParentId == root.Id && string.Equals(c.Name, child, StringComparison.OrdinalIgnoreCase)))
            {
                await ReplyAsync(seller, MessageTemplates.Get("category.003", label), ct);
                return;
            }
            _db.Categories.Add(new Category { SellerId = seller.Id, Name = child, Parent = root });
        }
        await _db.SaveChangesAsync(ct);
        await ReplyAsync(seller, MessageTemplates.Get("category.001", label, await CategoryListTextAsync(seller, ct)), ct);
    }
}
