using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// P1 of docs/design-learned-utterances.md: store masked messages the bot could not place. Changes no reply and runs nothing.
public partial class ConversationEngine
{
    private async Task RecordUnplacedAsync(Seller seller, string stage, string? text, string? routerIntent, double? routerConfidence, CancellationToken ct)
    {
        if (!_features.UnplacedCapture || string.IsNullOrWhiteSpace(text)) return;
        var hash = UnplacedSellerHash.For(_features.UnplacedCaptureSalt, seller.Id);
        if (hash is null) return;

        var customerNames = await _db.Customers.AsNoTracking()
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null)
            .Select(c => c.Name).Take(300).ToListAsync(ct);
        var productNames = await _db.Products.AsNoTracking()
            .Where(p => p.SellerId == seller.Id)
            .Select(p => p.Name).Take(300).ToListAsync(ct);

        _db.UnplacedUtterances.Add(new UnplacedUtterance
        {
            SellerHash = hash,
            MaskedText = UtteranceMasker.Mask(text, customerNames.Concat(productNames)),
            Stage = stage,
            RouterIntent = routerIntent,
            RouterConfidence = routerConfidence,
        });
        await _db.SaveChangesAsync(ct);
    }

}
