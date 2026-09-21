using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Read-only customer views (mockup screens 6c-6e), reached through the "Customers" menu.
public partial class ConversationEngine
{
    private const int CustomerPageSize = 10;
    private const int DefaultLoyalThreshold = 5;

    private IQueryable<Customer> SellerCustomers(Seller seller) =>
        _db.Customers.Include(c => c.Orders).Where(c => c.SellerId == seller.Id);

    private static int ActiveOrderCount(Customer c) => c.Orders.Count(o => o.Status != OrderStatus.Cancelled);
    private static decimal TotalSpent(Customer c) => c.Orders.Where(o => o.Status != OrderStatus.Cancelled).Sum(o => o.Total);

    private async Task HandleCustomerListAsync(Seller seller, SessionContextData ctx, CancellationToken ct)
    {
        var customers = (await SellerCustomers(seller).ToListAsync(ct))
            .OrderByDescending(ActiveOrderCount).ThenBy(c => c.Name).ToList();

        if (customers.Count == 0)
        {
            await ReplyAsync(seller, "Abhi koi customer nahi hai. Pehla order save karte hi customer list ban jati hai.", ct);
            return;
        }

        var page = customers.Take(CustomerPageSize).ToList();
        ctx.LastListCustomerIds = page.Select(c => c.Id).ToList();
        var lines = page.Select((c, i) => $"{i + 1} {c.Name} - {c.Phone ?? "—"} - {ActiveOrderCount(c)} orders");
        var more = customers.Count > page.Count ? $"\n(Top {page.Count} dikhaye — baaki ke liye naam ya \"search customer: naam\" likhein.)" : "";
        await ReplyAsync(seller,
            $"👥 Aapke Customers ({customers.Count} total):\n\n{string.Join("\n", lines)}{more}\n\n" +
            "\"customer 1\" ya naam likh kar detail dekhein.", ct);
    }

    private async Task HandleCustomerDetailAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        Customer? customer = null;
        if (cmd.Number is { } n && ctx.LastListCustomerIds is { } ids && n >= 1 && n <= ids.Count)
            customer = await SellerCustomers(seller).FirstOrDefaultAsync(c => c.Id == ids[n - 1], ct);

        if (customer is null)
        {
            var wanted = cmd.Text!.ToLower();
            customer = (await SellerCustomers(seller).ToListAsync(ct))
                .OrderByDescending(ActiveOrderCount)
                .FirstOrDefault(c => c.Name.ToLower().Contains(wanted) || (c.Phone ?? "").Contains(wanted));
        }

        if (customer is null)
        {
            await ReplyAsync(seller, $"\"{cmd.Text}\" naam ka koi customer nahi mila. \"customer list\" se dekhein.", ct);
            return;
        }

        var orders = customer.Orders.Where(o => o.Status != OrderStatus.Cancelled).OrderByDescending(o => o.CreatedAt).ToList();
        var threshold = (await _db.LoyaltyRules.Where(r => r.SellerId == seller.Id && r.IsActive).Select(r => (int?)r.OrderThreshold).FirstOrDefaultAsync(ct))
                        ?? DefaultLoyalThreshold;

        var lines = new List<string> { $"👤 {customer.Name}", "" };
        if (!string.IsNullOrWhiteSpace(customer.Phone)) lines.Add($"📞 {customer.Phone}");
        var place = string.Join(", ", new[] { customer.Address, customer.City }.Where(v => !string.IsNullOrWhiteSpace(v)));
        if (place.Length > 0) lines.Add($"📍 {place}");
        lines.Add($"🛍️ Total orders: {orders.Count}");
        lines.Add($"💰 Total spent: {Formatters.Money(orders.Sum(o => o.Total))}");
        if (orders.Count >= threshold) lines.Add("⭐ Status: Loyal customer");
        if (orders.Count > 0)
        {
            var days = (DateTime.UtcNow.Date - orders[0].CreatedAt.Date).Days;
            lines.Add($"🕐 Last order: {(days == 0 ? "aaj" : $"{days} din pehle")} ({Formatters.Status(orders[0].Status)})");
        }
        if (!string.IsNullOrWhiteSpace(customer.Notes)) lines.Add($"📝 {customer.Notes}");

        if (orders.Count > 0)
        {
            var recentIds = orders.Take(3).Select(x => x.Id).ToList();
            var recent = await _db.Orders.Include(o => o.Items).Where(o => recentIds.Contains(o.Id)).ToListAsync(ct);
            lines.Add("");
            lines.Add("Recent orders:");
            lines.AddRange(orders.Take(3).Select((o, i) =>
                $"{i + 1}. {Formatters.ItemsSummary(recent.First(x => x.Id == o.Id))} - {Formatters.Money(o.Total)} - {Formatters.Status(o.Status)}"));
        }

        await ReplyAsync(seller, string.Join("\n", lines), ct);
    }

    private async Task HandleCustomerSearchAsync(Seller seller, string query, CancellationToken ct)
    {
        var wanted = query.ToLower();
        var matches = (await SellerCustomers(seller).ToListAsync(ct))
            .Where(c => c.Name.ToLower().Contains(wanted) || (c.Phone ?? "").Contains(wanted))
            .OrderByDescending(ActiveOrderCount).Take(5).ToList();

        if (matches.Count == 0)
        {
            await ReplyAsync(seller, $"🔍 \"{query}\" se koi customer nahi mila.", ct);
            return;
        }

        var lines = matches.Select(c => $"👤 {c.Name} - {c.Phone ?? "—"} - {ActiveOrderCount(c)} orders - {Formatters.Money(TotalSpent(c))}");
        await ReplyAsync(seller,
            $"🔍 {matches.Count} customer mila:\n\n{string.Join("\n", lines)}\n\n\"customer {matches[0].Name.ToLower()}\" likh kar full detail dekhein.", ct);
    }
}
