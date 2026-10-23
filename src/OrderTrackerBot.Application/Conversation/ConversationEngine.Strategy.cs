using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Strategy;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// Strategy learning for the price guard: when the written price differs from the catalog, the bot asks (1 / 2 / 3). Each answer is counted per seller,
// and once one answer is reliable for this seller (StrategyPolicy) it is applied without the question. The draft still shows the price before YES.
// Corrections count against the answer: a draft edited or cancelled, or the order undone, after an auto-applied price. Off = always ask.
public partial class ConversationEngine
{
    private const string PriceContext = "price_guard";
    private const string AnyArm = "*";

    private static string PriceArm(string answer) => answer switch { "1" => "written", "2" => "catalog", _ => "update" };

    private static string PriceAnswer(string arm) => arm == "written" ? "1" : "2";

    /// <summary>The price answer ("1" / "2") this seller has given reliably enough to apply without asking, or null.</summary>
    private async Task<string?> UsualPriceAnswerAsync(Seller seller, CancellationToken ct)
    {
        if (!_features.StrategyLearning) return null;
        var rows = await _db.StrategyStats.Where(s => s.SellerId == seller.Id && s.Context == PriceContext).ToListAsync(ct);
        var total = rows.FirstOrDefault(r => r.Arm == AnyArm)?.Picked ?? 0;
        var arm = StrategyPolicy.Pick(rows.Where(r => r.Arm is "written" or "catalog")
            .Select(r => new StrategyPolicy.Count(r.Arm, r.Picked - r.Overridden, total)));
        return arm is null ? null : PriceAnswer(arm);
    }

    /// <summary>Applies answer 1 (the written price), 2 (catalog rate) or 3 (written price, catalog updated) to one draft line.</summary>
    private async Task ApplyPriceAnswerAsync(Seller seller, PendingOrderItemData item, string answer, CancellationToken ct)
    {
        if (answer is "1" or "3")
        {
            item.UnitPrice = item.StatedPrice!.Value;
            item.PriceDecided = true;
        }
        else item.WrittenPrice = null; // "2": the buyer's catalog rate applies from now on
        if (answer == "3" && item.ProductId is { } productId && await _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.SellerId == seller.Id, ct) is { } listed)
        {
            listed.Price = item.StatedPrice!.Value;
            await _db.SaveChangesAsync(ct);
        }
        item.StatedPrice = null;
    }

    /// <summary>Counts one decision: the answer was the final choice (asked or applied).</summary>
    private async Task RecordPriceDecisionAsync(Seller seller, string answer, CancellationToken ct)
    {
        await BumpStrategyAsync(seller, PriceContext, AnyArm, picked: 1, overridden: 0, ct);
        await BumpStrategyAsync(seller, PriceContext, PriceArm(answer), picked: 1, overridden: 0, ct);
    }

    /// <summary>A correction: an auto-applied answer was edited, cancelled or undone.</summary>
    private Task RetractPriceAnswerAsync(Seller seller, string arm, CancellationToken ct) =>
        BumpStrategyAsync(seller, PriceContext, arm, picked: 0, overridden: 1, ct);

    /// <summary>Corrections for every auto-applied price line of a draft that is being thrown away by "edit" or "cancel".</summary>
    private async Task RetractAutoPricesAsync(Seller seller, PendingOrderData? draft, CancellationToken ct)
    {
        if (!_features.StrategyLearning || draft is null) return;
        foreach (var arm in draft.Items.Select(i => i.AutoPriceArm).OfType<string>())
            await RetractPriceAnswerAsync(seller, arm, ct);
    }

    /// <summary>Remembers the auto-applied price of a saved draft, so undoing that order counts as a correction.</summary>
    private void RememberAutoPriceUse(SessionContextData ctx, PendingOrderData pending, Order order)
    {
        if (!_features.StrategyLearning) return;
        if (pending.Items.FirstOrDefault(i => i.AutoPriceArm is not null) is { AutoPriceArm: { } arm }) ctx.AutoPriceUse = new AutoPriceUse(order.Id, arm);
    }

    private async Task BumpStrategyAsync(Seller seller, string context, string arm, int picked, int overridden, CancellationToken ct)
    {
        if (!_features.StrategyLearning) return;
        var row = await _db.StrategyStats.FirstOrDefaultAsync(s => s.SellerId == seller.Id && s.Context == context && s.Arm == arm, ct);
        if (row is null)
        {
            row = new StrategyStat { SellerId = seller.Id, Context = context, Arm = arm };
            _db.StrategyStats.Add(row);
        }
        row.Picked += picked;
        row.Overridden += overridden;
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
    }
}
