using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// "setup status" / "is setup complete": answers from the seller's own record (OnboardingComplete on the seller row, the shop type and
/// the active products). Read only; nothing is changed.
/// </summary>
public partial class ConversationEngine
{
    private async Task HandleSetupStatusAsync(Seller seller, CancellationToken ct)
    {
        var products = await _db.Products.CountAsync(p => p.SellerId == seller.Id && p.IsActive, ct);
        var key = seller.OnboardingComplete ? "setup.status.done" : "setup.status.pending";
        await ReplyAsync(seller, MessageTemplates.Get(key, seller.BusinessName ?? "-", Industry(seller).Title, products), ct);
    }
}
