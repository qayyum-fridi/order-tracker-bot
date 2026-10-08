using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Reports that take "for how long": customers who ordered in a period ("customers last month", "pichle hafte ke customers").
public partial class ConversationEngine
{
    private async Task HandleCustomersByPeriodAsync(Seller seller, SessionContextData ctx, string period, CancellationToken ct)
    {
        var window = ReportPeriods.Resolve(period, seller.TimeZoneId, DateTime.UtcNow);
        if (window is null)
        {
            await HandleCustomerListAsync(seller, ctx, ct);
            return;
        }

        var label = PeriodLabel(seller, period);
        var orders = await _db.Orders.AsNoTracking().Include(o => o.Customer)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= window.StartUtc && o.CreatedAt < window.EndUtc
                        && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer != null && o.Customer.DeletedAt == null)
            .ToListAsync(ct);

        // Summed in memory (Sqlite can't SUM decimal).
        var ranked = orders.GroupBy(o => o.CustomerId)
            .Select(g => (Customer: g.First().Customer!, Orders: g.Count(), Spent: g.Sum(o => o.Total)))
            .OrderByDescending(x => x.Spent).ThenBy(x => x.Customer.Name).ToList();
        if (ranked.Count == 0)
        {
            await ReplyAsync(seller, $"👥 Customers ({label}): is period mein kisi customer ka order nahi aaya.", ct);
            return;
        }

        const int shown = 15;
        var top = ranked.Take(shown).ToList();
        ctx.LastListCustomerIds = top.Select(x => x.Customer.Id).ToList();
        var lines = top.Select((x, i) => $"{i + 1} {x.Customer.Name} - {x.Customer.Phone ?? "—"} - {x.Orders} orders - {Formatters.Money(x.Spent)}");
        var more = ranked.Count > shown ? $"\n…aur {ranked.Count - shown} customers" : "";
        await ReplyAsync(seller,
            $"👥 Customers ({label}) — {ranked.Count}, total {Formatters.Money(ranked.Sum(x => x.Spent))}\n\n{string.Join("\n", lines)}{more}\n\n\"customer 1\" ya naam likh kar detail dekhein.", ct);
    }
}
