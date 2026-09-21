using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Detailed entry via WhatsApp Flows (in-chat forms): "add product (detailed)", "add customer (detailed)", "new order (detailed)".
public partial class ConversationEngine
{
    private async Task HandleDetailedFormRequestAsync(Seller seller, string kind, CancellationToken ct)
    {
        var (body, cta, fallback) = kind switch
        {
            "product" => ("📋 Form khol raha hoon — sab details ek jagah bharein:", "Naya Product", "Kurti - 1800"),
            "customer" => ("📋 Customer ki details bharein:", "Naya Customer", "new order: Sara, 1 kurti, 03001234567"),
            _ => ("📋 Order ki details bharein:", "Naya Order", "new order: Sara, 1 kurti, 03001234567, Lahore")
        };

        var sent = await _sender.SendFlowMessageAsync(seller.WhatsAppPhoneNumber, kind, body, cta, ct);
        if (!sent)
            await ReplyAsync(seller, $"📋 Detailed form abhi setup nahi hua.\n\nTab tak seedha likhein:\n{fallback}", ct);
    }

    /// <summary>A submitted WhatsApp Flow form. The flow_token we sent ("product"/"customer"/"order") says which form it was.</summary>
    public async Task HandleFlowSubmissionAsync(string fromPhoneNumber, string responseJson, CancellationToken ct = default)
    {
        var seller = await _db.Sellers.Include(s => s.Session).FirstOrDefaultAsync(s => s.WhatsAppPhoneNumber == fromPhoneNumber, ct);
        if (seller is null || !seller.OnboardingComplete) return;

        JsonElement root;
        try { root = JsonDocument.Parse(responseJson).RootElement; }
        catch (JsonException) { return; }
        if (root.ValueKind != JsonValueKind.Object) return;

        string? Field(string name)
        {
            if (!root.TryGetProperty(name, out var v)) return null;
            var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        switch (Field("flow_token"))
        {
            case "product": await SaveProductFormAsync(seller, Field, ct); break;
            case "customer": await SaveCustomerFormAsync(seller, Field, ct); break;
            case "order": await SaveOrderFormAsync(seller, Field, ct); break;
            default: return;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task SaveProductFormAsync(Seller seller, Func<string, string?> field, CancellationToken ct)
    {
        var name = field("name");
        if (name is null || !decimal.TryParse(field("price"), NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
        {
            await ReplyAsync(seller, "Product ka naam aur price zaroori hain — form dobara bharein (\"add product (detailed)\").", ct);
            return;
        }

        var category = field("category");
        var size = field("size");
        var color = field("color");
        var stock = int.TryParse(field("stock"), out var s) ? s : (int?)null;

        var lower = name.ToLower();
        var existing = (await _db.Products.Where(p => p.SellerId == seller.Id && p.Name.ToLower() == lower).ToListAsync(ct))
            .FirstOrDefault(p => string.Equals(p.Color, color, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(p.Size, size, StringComparison.OrdinalIgnoreCase));

        var product = existing ?? new Product { SellerId = seller.Id, Name = name };
        product.Price = price;
        product.Category = category;
        product.Size = size;
        product.Color = color;
        product.StockQty = stock;
        if (existing is null) _db.Products.Add(product);

        await ReplyAsync(seller,
            $"✅ {Formatters.ProductLabel(product)} — {Formatters.Money(price)}{(stock is null ? "" : $", stock {stock}")} — " +
            (existing is null ? "catalog mein add ho gaya." : "update ho gaya."), ct);
    }

    private async Task SaveCustomerFormAsync(Seller seller, Func<string, string?> field, CancellationToken ct)
    {
        var name = field("name");
        var phone = field("phone");
        if (name is null || phone is null)
        {
            await ReplyAsync(seller, "Customer ka naam aur phone zaroori hain — form dobara bharein (\"add customer (detailed)\").", ct);
            return;
        }

        var lower = name.ToLower();
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.SellerId == seller.Id && (c.Phone == phone || c.Name.ToLower() == lower), ct);
        var isNew = customer is null;
        customer ??= new Customer { SellerId = seller.Id, Name = name };
        customer.Name = name;
        customer.Phone = phone;
        customer.City = field("city") ?? customer.City;
        customer.Address = field("address") ?? customer.Address;
        customer.PreferredContact = field("preferred_contact") ?? customer.PreferredContact;
        customer.Notes = field("notes") ?? customer.Notes;
        if (isNew) _db.Customers.Add(customer);

        await ReplyAsync(seller, $"✅ {name} ({phone}) customer list mein {(isNew ? "add ho gaya" : "update ho gaya")}.", ct);
    }

    private async Task SaveOrderFormAsync(Seller seller, Func<string, string?> field, CancellationToken ct)
    {
        var customerName = field("customer");
        var productText = field("product");
        if (customerName is null || productText is null)
        {
            await ReplyAsync(seller, "Customer aur product zaroori hain — form dobara bharein (\"new order (detailed)\").", ct);
            return;
        }

        var wanted = productText.ToLower();
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        var product = products.FirstOrDefault(p => Formatters.ProductLabel(p).ToLower() == wanted || p.Name.ToLower() == wanted)
                      ?? products.FirstOrDefault(p => Formatters.ProductLabel(p).ToLower().Contains(wanted) || wanted.Contains(p.Name.ToLower()));
        if (product is null)
        {
            await ReplyAsync(seller, $"\"{productText}\" catalog mein nahi mila — pehle \"add product (detailed)\" ya \"Naam - price\" se add karein.", ct);
            return;
        }

        var quantity = int.TryParse(field("quantity"), out var q) && q > 0 ? q : 1;
        var existingCustomer = await _db.Customers.FirstOrDefaultAsync(c => c.SellerId == seller.Id && c.Name.ToLower() == customerName.ToLower(), ct);
        var payment = field("payment_method") ?? "COD";
        var total = product.Price * quantity;

        var pending = new PendingOrderData
        {
            CustomerName = customerName,
            Phone = existingCustomer?.Phone,
            Address = existingCustomer?.Address,
            PaymentMethodText = payment,
            Items = { new PendingOrderItemData { ProductName = product.Name, Quantity = quantity, UnitPrice = product.Price } },
            Subtotal = total,
            Total = total
        };

        var order = await SaveOrderFromDraftAsync(seller, pending, ct);
        order.PaymentMethod = payment.ToLowerInvariant() switch
        {
            var p when p.Contains("cod") => OrderPaymentMethod.Cod,
            var p when p.Contains("jazz") || p.Contains("easy") || p.Contains("bank") => OrderPaymentMethod.Manual,
            _ => order.PaymentMethod
        };
        order.DeliveryDate = ParseFlowDate(field("delivery_date"));
        order.OrderSource = field("order_source");
        order.Notes = field("notes");
        await _db.SaveChangesAsync(ct);
        await CheckLoyaltyThresholdAsync(seller, order.CustomerId, ct);

        await ReplyAsync(seller,
            $"✅ Order saved (#{order.Id}) — {customerName}, {Formatters.ProductLabel(product)} x{quantity}, {Formatters.Money(total)}, {payment}" +
            (order.DeliveryDate is { } d ? $", delivery {d:dd MMM}." : "."), ct);
    }

    // Flow date pickers send "yyyy-MM-dd"; calendar pickers send epoch milliseconds.
    private static DateTime? ParseFlowDate(string? value)
    {
        if (value is null) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return date;
        return long.TryParse(value, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
    }
}
