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

    private const int RestoreWindowDays = 30;

    private IQueryable<Customer> SellerCustomers(Seller seller) =>
        _db.Customers.Include(c => c.Orders).Where(c => c.SellerId == seller.Id && c.DeletedAt == null);

    private static int ActiveOrderCount(Customer c) => c.Orders.Count(o => o.Status != OrderStatus.Cancelled);
    private static decimal TotalSpent(Customer c) => c.Orders.Where(o => o.Status != OrderStatus.Cancelled).Sum(o => o.Total);

    private async Task HandleCustomerListAsync(Seller seller, SessionContextData ctx, CancellationToken ct, int page = 0)
    {
        var customers = (await SellerCustomers(seller).ToListAsync(ct))
            .OrderByDescending(ActiveOrderCount).ThenBy(c => c.Name).ToList();

        if (customers.Count == 0)
        {
            await ReplyAsync(seller, "Abhi koi customer nahi hai. Pehla order save karte hi customer list ban jati hai.", ct);
            return;
        }

        if (page * CustomerPageSize >= customers.Count) page = 0;
        var shown = customers.Skip(page * CustomerPageSize).Take(CustomerPageSize).ToList();
        ctx.CustomerListPage = page;
        ctx.LastListCustomerIds = shown.Select(c => c.Id).ToList();
        var lines = shown.Select((c, i) => $"{i + 1} {c.Name} - {c.Phone ?? "—"} - {ActiveOrderCount(c)} orders");
        var hasMore = customers.Count > (page + 1) * CustomerPageSize;
        await ReplyAsync(seller,
            $"👥 Aapke Customers ({customers.Count} total){(page > 0 ? $" — page {page + 1}" : "")}:\n\n{string.Join("\n", lines)}\n\n" +
            "\"customer 1\" ya naam likh kar detail dekhein." + (hasMore ? $" \"more\" agle {CustomerPageSize} ke liye." : ""), ct);
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

        lines.Add("");
        var listIndex = ctx.LastListCustomerIds?.IndexOf(customer.Id) ?? -1;
        lines.Add($"\"delete customer {(listIndex >= 0 ? (listIndex + 1).ToString() : customer.Name.ToLower())}\" se remove kar saktay hain.");
        await ReplyAsync(seller, string.Join("\n", lines), ct);
    }

    private async Task<Customer?> FindCustomerAsync(Seller seller, SessionContextData ctx, string text, int? number, CancellationToken ct)
    {
        if (number is { } n && ctx.LastListCustomerIds is { } ids && n >= 1 && n <= ids.Count)
            return await SellerCustomers(seller).FirstOrDefaultAsync(c => c.Id == ids[n - 1], ct);

        var wanted = text.ToLower();
        var all = await SellerCustomers(seller).ToListAsync(ct);
        return all.FirstOrDefault(c => c.Name.ToLower() == wanted || c.Phone == text)
               ?? all.OrderByDescending(ActiveOrderCount).FirstOrDefault(c => c.Name.ToLower().Contains(wanted));
    }

    // Screen 6f: soft delete with confirm; restorable for 30 days, then purged along with its orders.
    private async Task HandleDeleteCustomerRequestAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var customer = await FindCustomerAsync(seller, ctx, cmd.Text!, cmd.Number, ct);
        if (customer is null)
        {
            await ReplyAsync(seller, $"\"{cmd.Text}\" naam ka koi customer nahi mila. \"customer list\" se dekhein.", ct);
            return;
        }

        ctx.DeleteCustomerId = customer.Id;
        SetState(session, ConversationState.AwaitingDeleteCustomerConfirmation);
        await ReplyAsync(seller,
            $"⚠️ {customer.Name} ({ActiveOrderCount(customer)} orders, {Formatters.Money(TotalSpent(customer))}) ko remove karna confirm karein?\n" +
            $"Order history hide ho jayegi lekin permanently delete nahi hogi — {RestoreWindowDays} din tak restore kar saktay hain.\n\nReply YES to confirm.", ct);
    }

    private async Task HandleDeleteCustomerConfirmationAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var id = ctx.DeleteCustomerId;
        ctx.DeleteCustomerId = null;
        SetState(session, ConversationState.Idle);

        if (!CommandParser.IsAffirmative(message))
        {
            await ReplyAsync(seller, "Theek hai, customer remove nahi kiya.", ct);
            return;
        }

        await PurgeExpiredCustomersAsync(seller, ct);
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id && c.SellerId == seller.Id, ct);
        if (customer is null) return;

        customer.DeletedAt = DateTime.UtcNow;
        await ReplyAsync(seller,
            $"✅ {customer.Name} remove kar diya gaya. \"restore customer {customer.Name.ToLower()}\" se {RestoreWindowDays} din tak wapas la saktay hain — " +
            $"uske baad permanently delete ho jayega.", ct);
    }

    private async Task HandleRestoreCustomerAsync(Seller seller, string name, CancellationToken ct)
    {
        var wanted = name.ToLower();
        var cutoff = DateTime.UtcNow.AddDays(-RestoreWindowDays);
        var customer = (await _db.Customers.Include(c => c.Orders)
                .Where(c => c.SellerId == seller.Id && c.DeletedAt != null && c.DeletedAt >= cutoff).ToListAsync(ct))
            .OrderByDescending(c => c.DeletedAt)
            .FirstOrDefault(c => c.Name.ToLower() == wanted || c.Name.ToLower().Contains(wanted) || c.Phone == name);

        if (customer is null)
        {
            await ReplyAsync(seller, $"\"{name}\" naam ka koi removed customer nahi mila (ya {RestoreWindowDays} din guzar gaye).", ct);
            return;
        }

        customer.DeletedAt = null;
        await ReplyAsync(seller, $"✅ {customer.Name} wapas active ho gaya — {ActiveOrderCount(customer)} orders aur history restore ho gayi.", ct);
    }

    private async Task PurgeExpiredCustomersAsync(Seller seller, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RestoreWindowDays);
        var expired = await _db.Customers.Where(c => c.SellerId == seller.Id && c.DeletedAt != null && c.DeletedAt < cutoff).ToListAsync(ct);
        if (expired.Count == 0) return;
        var ids = expired.Select(c => c.Id).ToList();
        _db.Orders.RemoveRange(await _db.Orders.Where(o => ids.Contains(o.CustomerId)).ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
        _db.Customers.RemoveRange(expired);
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
        var partial = matches.All(c => !c.Name.Equals(query, StringComparison.OrdinalIgnoreCase) && c.Phone != query) ? $" (partial match \"{query}\")" : "";
        await ReplyAsync(seller,
            $"🔍 {matches.Count} customer mila{partial}:\n\n{string.Join("\n", lines)}\n\n\"customer {matches[0].Name.ToLower()}\" likh kar full detail dekhein.", ct);
    }
}
