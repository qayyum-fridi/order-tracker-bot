using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// "supplier Lawn Suit Shah Alam" / "supplier Shah Alam: Lawn Suit, Kurti" / "Lawn Suit ka supplier Shah Alam" / "supplier remove Lawn Suit":
// the supplier is a seller-only product attribute ("supplier" in Product.AttributesJson, the same place "Lawn Suit - 3500, supplier: Shah Alam" writes it),
// so the supplier report ("kis supplier ka maal zyada bika") groups by it. This command only saves typing the whole product line again.
// "courier fee 12 250" / "order 12 courier fee 250": what the courier charged for the parcel (Order.CourierFee); shown in the return-loss and courier-fee reports.
public partial class ConversationEngine
{
    private const int MaxSupplierProducts = 30;

    private static string? SupplierOf(Product product)
    {
        if (string.IsNullOrWhiteSpace(product.AttributesJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(product.AttributesJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Name.Equals("supplier", StringComparison.OrdinalIgnoreCase)) return prop.Value.ToString().Trim() is { Length: > 0 } v ? v : null;
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>The attributes with "supplier" set (or removed when <paramref name="supplier"/> is null); every other attribute is kept as it was.</summary>
    private static string? WithSupplier(string? attributesJson, string? supplier)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(attributesJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(attributesJson);
                foreach (var prop in doc.RootElement.EnumerateObject()) attributes[prop.Name] = prop.Value.ToString();
            }
            catch (JsonException) { }
        }
        attributes.Remove("supplier");
        if (supplier is not null) attributes["supplier"] = supplier;
        return attributes.Count == 0 ? null : JsonSerializer.Serialize(attributes);
    }

    private static bool StartsWithWord(string text, string name) =>
        text.StartsWith(name, StringComparison.OrdinalIgnoreCase) && (text.Length == name.Length || !char.IsLetterOrDigit(text[name.Length]));

    private static bool EndsWithWord(string text, string name) =>
        text.EndsWith(name, StringComparison.OrdinalIgnoreCase) && (text.Length == name.Length || !char.IsLetterOrDigit(text[text.Length - name.Length - 1]));

    private static string CleanSupplierName(string value) => value.Trim().Trim(':', '-', '–', ',', ';', '=').Trim();

    private async Task HandleSupplierSetAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Id).ToListAsync(ct);
        if (products.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("supplier.001"), ct);
            return;
        }

        Product? Find(string name)
        {
            var wanted = name.Trim();
            return products.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(Formatters.ProductLabel(p), wanted, StringComparison.OrdinalIgnoreCase))
                   ?? (TextDistance.Closest(wanted, products.Select(p => p.Name)) is { } near ? products.First(p => p.Name.Equals(near, StringComparison.OrdinalIgnoreCase)) : null);
        }

        var usage = MessageTemplates.Get("supplier.014");
        var targets = new List<Product>();
        string? supplier = null;

        if (cmd.Text2 == "remove")
        {
            var removing = Find(cmd.Text ?? "");
            if (removing is null)
            {
                await ReplyAsync(seller, MessageTemplates.Get("supplier.002", cmd.Text), ct);
                return;
            }
            var before = SupplierOf(removing);
            removing.AttributesJson = WithSupplier(removing.AttributesJson, null);
            await ReplyAsync(seller, before is null ? MessageTemplates.Get("supplier.011", Formatters.ProductLabel(removing)) : MessageTemplates.Get("supplier.012", Formatters.ProductLabel(removing), before), ct);
            return;
        }

        if (cmd.Text2 == "of")
        {
            var one = Find(cmd.Text ?? "");
            supplier = CleanSupplierName(cmd.Text3 ?? "");
            if (one is null)
            {
                await ReplyAsync(seller, MessageTemplates.Get("supplier.002", cmd.Text), ct);
                return;
            }
            targets.Add(one);
        }
        else
        {
            var rest = (cmd.Text ?? "").Trim();
            var colon = rest.IndexOf(':');
            if (colon > 0)
            {
                var left = rest[..colon].Trim();
                var right = rest[(colon + 1)..].Trim();
                if (Find(left) is { } leftProduct && right.Length > 0 && !right.Contains(','))
                {
                    targets.Add(leftProduct);
                    supplier = CleanSupplierName(right);
                }
                else
                {
                    supplier = CleanSupplierName(left);
                    foreach (var name in right.Split(new[] { ',', '،', '+', '&' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var found = Find(name);
                        if (found is null)
                        {
                            await ReplyAsync(seller, MessageTemplates.Get("supplier.003", name), ct);
                            return;
                        }
                        if (!targets.Contains(found)) targets.Add(found);
                    }
                }
            }
            else
            {
                var byPrefix = products.Where(p => StartsWithWord(rest, p.Name)).OrderByDescending(p => p.Name.Length).FirstOrDefault();
                var bySuffix = products.Where(p => EndsWithWord(rest, p.Name)).OrderByDescending(p => p.Name.Length).FirstOrDefault();
                if (byPrefix is not null && CleanSupplierName(rest[byPrefix.Name.Length..]).Length > 0)
                {
                    targets.Add(byPrefix);
                    supplier = CleanSupplierName(rest[byPrefix.Name.Length..]);
                }
                else if (bySuffix is not null && CleanSupplierName(rest[..^bySuffix.Name.Length]).Length > 0)
                {
                    targets.Add(bySuffix);
                    supplier = CleanSupplierName(rest[..^bySuffix.Name.Length]);
                }
            }
        }

        if (targets.Count == 0 || string.IsNullOrWhiteSpace(supplier))
        {
            await ReplyAsync(seller, MessageTemplates.Get("supplier.004", usage), ct);
            return;
        }
        if (supplier.Length > 60 || targets.Count > MaxSupplierProducts)
        {
            await ReplyAsync(seller, MessageTemplates.Get("supplier.005", MaxSupplierProducts), ct);
            return;
        }

        var lines = new List<string>();
        foreach (var product in targets)
        {
            var old = SupplierOf(product);
            product.AttributesJson = WithSupplier(product.AttributesJson, supplier);
            lines.Add(old is null ? $"• {Formatters.ProductLabel(product)}" : old.Equals(supplier, StringComparison.OrdinalIgnoreCase) ? $"• {Formatters.ProductLabel(product)} (pehle se yehi tha)" : $"• {Formatters.ProductLabel(product)} (pehle: {old})");
        }
        await ReplyAsync(seller,
            MessageTemplates.Get("supplier.006", supplier, string.Join("\n", lines)), ct);
        SuggestNext("suppliers", "catalog");
    }

    // ---------- courier fee

    private async Task HandleOrderCourierFeeAsync(Seller seller, SessionContextData ctx, int number, decimal amount, CancellationToken ct)
    {
        var (orderId, fromList) = ResolveListNumber(ctx, number);
        var order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("commands.028", number), ct);
            return;
        }
        if (amount > 100000)
        {
            await ReplyAsync(seller, MessageTemplates.Get("supplier.007"), ct);
            return;
        }

        var before = order.CourierFee;
        order.CourierFee = amount;
        var gap = order.DeliveryCharge - amount;
        await ReplyAsync(seller,
            MessageTemplates.Get("supplier.008", order.Id, order.Customer?.Name, Formatters.Money(amount), (before is { } old && old != amount ? $" (pehle {Formatters.Money(old)})" : ""), (order.DeliveryCharge > 0 || amount > 0
                ? $"Customer se delivery {Formatters.Money(order.DeliveryCharge)} li → " + (gap > 0 ? $"{Formatters.Money(gap)} bach gaye." : gap < 0 ? $"{Formatters.Money(-gap)} aap ki jeb se gaye." : "barabar.") + "\n"
                : ""), (fromList ? $"\nℹ️ \"{number}\" aapki last list ka number tha." : "")), ct);
        SuggestNext("courier fee report", "return rate");
    }

    /// <summary>"Courier ko kitna diya": fees written down for the period against the delivery charges those orders carried, per courier.</summary>
    private async Task InsightCourierFeesAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && (o.Status != OrderStatus.Cancelled || o.CourierFee != null))
            .Select(o => new { o.TrackingCourier, o.CourierFee, o.DeliveryCharge, o.Status, o.ShippedAt })
            .ToListAsync(ct);
        var shipped = rows.Where(r => r.ShippedAt != null || r.Status is OrderStatus.Shipped or OrderStatus.Delivered or OrderStatus.Returned || r.CourierFee != null).ToList();
        var known = shipped.Where(r => r.CourierFee != null).ToList();
        if (known.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("supplier.009", label), ct);
            return;
        }

        var paid = known.Sum(r => r.CourierFee!.Value);
        var charged = known.Sum(r => r.DeliveryCharge);
        var groups = known.GroupBy(r => string.IsNullOrWhiteSpace(r.TrackingCourier) ? "Courier likha nahi" : r.TrackingCourier!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Parcels: g.Count(), Fee: g.Sum(r => r.CourierFee!.Value), Charged: g.Sum(r => r.DeliveryCharge)))
            .OrderByDescending(g => g.Fee).ToList();
        var lines = groups.Select(g => MessageTemplates.Get("supplier.013", g.Name, g.Parcels, Formatters.Money(g.Fee), Formatters.Money(g.Charged), (g.Charged - g.Fee >= 0 ? $"{Formatters.Money(g.Charged - g.Fee)} bacha" : $"{Formatters.Money(g.Fee - g.Charged)} aap ki jeb se")));
        var missing = shipped.Count - known.Count;
        await ReplyAsync(seller,
            MessageTemplates.Get("supplier.010", label, string.Join("\n", lines), Formatters.Money(paid), Formatters.Money(charged), (charged - paid >= 0 ? $"{Formatters.Money(charged - paid)} bacha" : $"{Formatters.Money(paid - charged)} aap ki jeb se gaye"), (missing > 0 ? $"\nℹ️ {missing} bheje gaye parcels ki fee likhi nahi, wo is hisaab mein nahi." : "")), ct);
        SuggestNext("return rate", "expenses");
    }
}
