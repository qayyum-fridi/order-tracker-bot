using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// "ready to wear category banao": a category is the product's Category text, so it exists once a product carries it.
// This answers how to add it and lists the categories already in the catalog. Nothing is written.
public partial class ConversationEngine
{
    private async Task HandleCreateCategoryAsync(Seller seller, string name, CancellationToken ct)
    {
        var existing = await _db.Products
            .Where(p => p.SellerId == seller.Id && p.Category != null)
            .Select(p => p.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);
        var list = existing.Count == 0 ? MessageTemplates.Get("category.002") : string.Join(", ", existing);
        await ReplyAsync(seller, MessageTemplates.Get("category.001", name.Trim(), list), ct);
    }
}
