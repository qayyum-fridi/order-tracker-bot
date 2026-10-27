using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>Body of a bulk product change. Action is hide, show or category; category is used only by category.</summary>
public sealed record ClientProductBulkInput(IReadOnlyList<int>? Ids, string? Action, string? Category);

/// <summary>Hides, shows or re-categorises several of the seller's products at once. Ids that belong to another shop are skipped.</summary>
public sealed class ClientProductBulk(AppDbContext db)
{
    public const int MaxItems = 200;
    private static readonly string[] Actions = ["hide", "show", "category"];

    /// <summary>The first problem found, in words a shop owner can act on. Null when the input is fine.</summary>
    public static string? Validate(ClientProductBulkInput input)
    {
        if (input.Ids is null || input.Ids.Count == 0) return "Select at least one product.";
        if (input.Ids.Count > MaxItems) return $"Change at most {MaxItems} products at a time.";
        if (input.Ids.Any(id => id <= 0)) return "Product numbers must be positive.";
        if (input.Action is null || !Actions.Contains(input.Action)) return "Choose hide, show or category.";
        if (input.Action == "category" && (input.Category?.Trim().Length ?? 0) > 60) return "Keep the category to 60 characters or fewer.";
        return null;
    }

    public async Task<int> BulkAsync(int sellerId, ClientProductBulkInput input, CancellationToken ct)
    {
        var ids = input.Ids!.Distinct().ToList();
        var products = await db.Products.Where(p => p.SellerId == sellerId && ids.Contains(p.Id)).ToListAsync(ct);
        var category = input.Category?.Trim();
        foreach (var p in products)
        {
            switch (input.Action)
            {
                case "hide": p.IsActive = false; break;
                case "show": p.IsActive = true; break;
                case "category": p.Category = string.IsNullOrEmpty(category) ? null : category; break;
            }
        }
        await db.SaveChangesAsync(ct);
        return products.Count;
    }
}
