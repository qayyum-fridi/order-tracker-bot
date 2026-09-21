using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private const string HelpText =
        "🆘 Yeh commands try karein:\n\n" +
        "• \"new order: naam, product, phone, address\"\n" +
        "• \"orders today\"\n" +
        "• \"today's summary\"\n" +
        "• \"pending orders\"\n" +
        "• \"[naam] ka order\"\n" +
        "• \"mark [number] shipped/delivered\"\n" +
        "• \"add tracking: courier, number\"\n" +
        "• \"[naam] ka tracking\"\n" +
        "• \"cod pending\"\n" +
        "• \"catalog\"\n" +
        "• \"add product: naam - price\"\n" +
        "• \"payment link\"\n" +
        "• \"unpaid orders\"\n" +
        "• \"trending products\"\n" +
        "• \"slow movers\"\n" +
        "• \"discount performance\"\n" +
        "• \"loyal customers\"\n" +
        "• \"create discount\"\n" +
        "• \"feedback: [your message]\"\n" +
        "• \"undo\"\n\n" +
        "Ya \"menu\" likh kar categorized list dekhein.";

    private const string MenuText =
        "📋 Main Menu\n\n" +
        "📦 Orders\n • new order: [details]\n • orders today / pending orders\n • [customer] ka order\n\n" +
        "📊 Reports\n • today's summary\n • trending products\n • slow movers\n • loyal customers\n • cod pending\n\n" +
        "🛍️ Catalog\n • catalog\n • add/edit product\n\n" +
        "💰 Payments\n • payment link\n • mark [order] paid\n\n" +
        "🎟️ Discounts\n • create discount\n • discount list\n\n" +
        "Command type karein, ya poochein.";

    // Undo is deliberately not tappable: an accidental tap would silently revert the last action.
    private static readonly IReadOnlyList<MenuSection> HelpSections = new[]
    {
        new MenuSection("📦 Orders", new[]
        {
            new MenuRow("orders today", "Orders today"),
            new MenuRow("pending orders", "Pending orders"),
            new MenuRow("today's summary", "Today's summary")
        }),
        new MenuSection("💰 Payments", new[]
        {
            new MenuRow("unpaid orders", "Unpaid orders"),
            new MenuRow("cod pending", "COD pending"),
            new MenuRow("payment link", "Payment link")
        }),
        new MenuSection("🛍️ More", new[]
        {
            new MenuRow("catalog", "Catalog"),
            new MenuRow("discount list", "Discount list"),
            new MenuRow("loyal customers", "Loyal customers"),
            new MenuRow("menu", "Main menu")
        })
    };

    private static readonly IReadOnlyList<MenuSection> MenuSections = new[]
    {
        new MenuSection("📦 Orders", new[]
        {
            new MenuRow("orders today", "Orders today"),
            new MenuRow("pending orders", "Pending orders"),
            new MenuRow("unpaid orders", "Unpaid orders")
        }),
        new MenuSection("📊 Reports", new[]
        {
            new MenuRow("today's summary", "Today's summary"),
            new MenuRow("trending products", "Trending products"),
            new MenuRow("slow movers", "Slow movers"),
            new MenuRow("loyal customers", "Loyal customers"),
            new MenuRow("cod pending", "COD pending")
        }),
        new MenuSection("🛍️ Catalog & Discounts", new[]
        {
            new MenuRow("catalog", "Catalog"),
            new MenuRow("discount list", "Discount list")
        })
    };

    private async Task ExecuteCommandAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        switch (cmd.Kind)
        {
            case CommandKind.Greeting:
                await HandleGreetingAsync(seller, ct);
                return;
            case CommandKind.Help:
                await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, HelpText, "Commands dekhein", HelpSections, ct);
                return;
            case CommandKind.Menu:
                await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, MenuText, "Menu kholein", MenuSections, ct);
                return;
            case CommandKind.OrdersToday:
                await HandleOrdersTodayAsync(seller, ct);
                return;
            case CommandKind.PendingOrders:
                await HandlePendingOrdersAsync(seller, ct);
                return;
            case CommandKind.TodaysSummary:
                await HandleTodaysSummaryAsync(seller, ct);
                return;
            case CommandKind.Catalog:
                await HandleCatalogAsync(seller, ct);
                return;
            case CommandKind.AddProduct:
                await HandleAddProductAsync(seller, cmd, ct);
                return;
            case CommandKind.HowTo:
                if (cmd.Text == "discount") SetState(session, ConversationState.AwaitingDiscountDetails);
                await ReplyAsync(seller, HowToText(cmd.Text!), ct);
                return;
            case CommandKind.AddProductsBulk:
                await HandleAddProductsBulkAsync(seller, cmd, ct);
                return;
            case CommandKind.EditProduct:
                await HandleEditProductAsync(seller, cmd, ct);
                return;
            case CommandKind.MarkStatus:
                await HandleMarkStatusAsync(seller, session, ctx, cmd, ct);
                return;
            case CommandKind.MarkAllPendingShipped:
                await HandleMarkAllPendingShippedAsync(seller, session, ctx, ct);
                return;
            case CommandKind.CancelOrder:
                await HandleCancelOrderRequestAsync(seller, session, ctx, cmd, ct);
                return;
            case CommandKind.Undo:
                await HandleUndoAsync(seller, ct);
                return;
            case CommandKind.PaymentLink:
                await HandlePaymentLinkAsync(seller, cmd, ct);
                return;
            case CommandKind.AddPaymentMethod:
                await HandleAddPaymentMethodAsync(seller, cmd, ct);
                return;
            case CommandKind.UnpaidOrders:
                await HandleUnpaidOrdersAsync(seller, ct);
                return;
            case CommandKind.CodPending:
                ctx.RuntimeFilterCommand = "cod";
                SetState(session, ConversationState.AwaitingRuntimeFilterChoice);
                await ReplyAsync(seller, "💵 Kitne purane pending dekhne hain?\n1️⃣ All\n2️⃣ 3+ days\n3️⃣ 7+ days", ct);
                return;
            case CommandKind.AddTracking:
                await HandleAddTrackingAsync(seller, cmd, ct);
                return;
            case CommandKind.TrackingLookup:
                await HandleTrackingLookupAsync(seller, cmd, ct);
                return;
            case CommandKind.CustomerOrderLookup:
                await HandleCustomerOrderLookupAsync(seller, cmd, ct);
                return;
            case CommandKind.FuzzyStatusUpdate:
                await HandleFuzzyStatusUpdateAsync(seller, session, ctx, cmd, ct);
                return;
            case CommandKind.CreateDiscount:
                await HandleCreateDiscountAsync(seller, cmd, ct);
                return;
            case CommandKind.DiscountList:
                await HandleDiscountListAsync(seller, session, ct);
                return;
            case CommandKind.CreateLoyalty:
                await HandleCreateLoyaltyAsync(seller, cmd, ct);
                return;
            case CommandKind.LoyalCustomers:
                await HandleLoyalCustomersAsync(seller, ct);
                return;
            case CommandKind.TrendingProducts:
                ctx.RuntimeFilterCommand = "trending";
                SetState(session, ConversationState.AwaitingRuntimeFilterChoice);
                await ReplyAsync(seller, "📈 Konsi time period dekhna chahte hain?\n1️⃣ This Week\n2️⃣ This Month\n3️⃣ Custom Dates", ct);
                return;
            case CommandKind.SlowMovers:
                ctx.RuntimeFilterCommand = "slow";
                SetState(session, ConversationState.AwaitingRuntimeFilterChoice);
                await ReplyAsync(seller, "📉 Kitne din se koi order nahi aaya?\n1️⃣ 7 days\n2️⃣ 14 days\n3️⃣ 30 days", ct);
                return;
            case CommandKind.ResetAccount:
                await StartResetAsync(seller, session, ct);
                return;
            case CommandKind.CustomerFeedbackList:
                await HandleCustomerFeedbackListAsync(seller, ct);
                return;
            case CommandKind.Feedback:
                await HandleFeedbackAsync(seller, cmd, ct);
                return;
            case CommandKind.Broadcast:
                await HandleBroadcastRequestAsync(seller, session, ctx, cmd, ct);
                return;
            default:
                await ReplyAsync(seller, "Samajh nahi aaya 🤔 \"help\" likh kar commands dekhein.", ct);
                return;
        }
    }

    private async Task HandleGreetingAsync(Seller seller, CancellationToken ct)
    {
        var pending = await _db.Orders.CountAsync(o => o.SellerId == seller.Id && o.Status == OrderStatus.Pending, ct);
        var unpaid = await _db.Orders.CountAsync(o => o.SellerId == seller.Id && o.PaymentStatus == PaymentStatus.Unpaid && o.Status != OrderStatus.Cancelled, ct);
        await ReplyAsync(seller, Formatters.ReturningGreeting(seller.PreferredLanguage, seller.BusinessName, pending, unpaid), ct);
    }

    private async Task HandleOrdersTodayAsync(Seller seller, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var orders = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= today && o.Status != OrderStatus.Cancelled)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct);

        await ReplyAsync(seller, Formatters.OrdersToday(seller.PreferredLanguage, orders), ct);
    }

    private async Task HandlePendingOrdersAsync(Seller seller, CancellationToken ct)
    {
        var orders = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.Status == OrderStatus.Pending)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct);

        if (orders.Count == 0)
        {
            await ReplyAsync(seller, "Koi pending order nahi hai.", ct);
            return;
        }

        var lines = orders.Select((o, i) => Formatters.OrderLine(i + 1, o));
        await ReplyAsync(seller, $"📦 Pending Orders ({orders.Count}):\n\n{string.Join("\n", lines)}", ct);
    }

    private async Task HandleTodaysSummaryAsync(Seller seller, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var orders = await _db.Orders
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= today && o.Status != OrderStatus.Cancelled)
            .ToListAsync(ct);

        var delivered = orders.Count(o => o.Status == OrderStatus.Delivered);
        var shipped = orders.Count(o => o.Status == OrderStatus.Shipped);
        var pending = orders.Count(o => o.Status == OrderStatus.Pending);
        var cod = orders.Where(o => o.PaymentMethod == OrderPaymentMethod.Cod && o.PaymentStatus == PaymentStatus.Paid).Sum(o => o.Total);
        var prepaid = orders.Where(o => o.PaymentMethod != OrderPaymentMethod.Cod && o.PaymentStatus == PaymentStatus.Paid).Sum(o => o.Total);
        var totalSales = orders.Sum(o => o.Total);

        await ReplyAsync(seller,
            $"🧾 Today's Reconciliation — {seller.BusinessName}\n\n" +
            $"Orders: {orders.Count}\n" +
            $"Delivered: {delivered} | Shipped: {shipped} | Pending: {pending}\n" +
            $"Cash collected (COD): {Formatters.Money(cod)}\n" +
            $"Prepaid received: {Formatters.Money(prepaid)}\n" +
            $"Total sales today: {Formatters.Money(totalSales)}\n\n" +
            "Sab theek lag raha hai ✅", ct);
    }

    private async Task HandleCatalogAsync(Seller seller, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive)
            .OrderBy(p => p.Id).ToListAsync(ct);

        if (products.Count == 0)
        {
            await ReplyAsync(seller,
                "🛍️ Aapka catalog abhi khali hai.\n\n" +
                "Product add karna bohot aasan hai — bas naam aur price bhejein:\n" +
                "Lawn Suit - 3500\n\n" +
                "Ek saath kai products bhi bhej saktay hain (har line mein ek):\n" +
                "Lawn Suit - 3500\nKurti - 1800\nDupatta - 900", ct);
            return;
        }

        var lines = products.Select((p, i) => $"{i + 1} {p.Name} - {Formatters.Money(p.Price)}");
        await ReplyAsync(seller,
            $"🛍️ Aapka Catalog ({products.Count} products):\n\n{string.Join("\n", lines)}\n\n" +
            "Naya product add karne ke liye bas likhein: Kurti - 1800", ct);
    }

    private static string HowToText(string subject) => subject switch
    {
        "discount" =>
            "🎟️ Discount code banane ke liye copy karke bhejein:\n\n" +
            "create discount: EID10, 10 percent, expires 15 days\n\n" +
            "Ya flat amount:\n" +
            "create discount: WELCOME50, Rs.50 flat\n\n" +
            "Ya seedha sirf code ka naam bhejein (e.g. EID10) — main value pooch loon ga. \"cancel\" likh kar ruk saktay hain.",
        "product" =>
            "🛍️ Product add karne ke liye bas likhein:\n\nKurti - 1800\n\n" +
            "Ek saath kai bhi bhej saktay hain (har line mein ek).",
        "payment" =>
            "💳 Payment method save karne ke liye likhein:\n\n" +
            "add payment: jazzcash, 0300-1234567\nadd payment: easypaisa, 0300-1234567",
        "loyalty" =>
            "⭐ Loyalty rule banane ke liye likhein:\n\ncreate loyalty: 5 orders = 10 percent off",
        _ =>
            "🚚 Tracking number save karne ke liye likhein:\n\nadd tracking: Leopards, LC998877"
    };

    private async Task HandleAddProductAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var (name, price, updated) = await UpsertProductAsync(seller, cmd.Text!, cmd.Amount!.Value, ct);
        await ReplyAsync(seller, updated
            ? $"✅ {name} price updated: {Formatters.Money(price)}"
            : $"✅ {name} - {Formatters.Money(price)} catalog mein add ho gaya.\n\nAur add karein (Naam - price), ya \"catalog\" likhein.", ct);
    }

    private async Task HandleAddProductsBulkAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var results = new List<string>();
        foreach (var line in cmd.Text!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!CommandParser.TryParseProductLine(line, out var lineName, out var linePrice)) continue;
            var (name, price, updated) = await UpsertProductAsync(seller, lineName, linePrice, ct);
            results.Add($"{results.Count + 1} {name} - {Formatters.Money(price)}{(updated ? " (updated)" : "")}");
        }
        await ReplyAsync(seller, $"✅ {results.Count} products save ho gaye:\n\n{string.Join("\n", results)}\n\n\"catalog\" likh kar poori list dekhein.", ct);
    }

    private async Task<(string Name, decimal Price, bool Updated)> UpsertProductAsync(Seller seller, string name, decimal price, CancellationToken ct)
    {
        var lower = name.ToLower();
        var existing = await _db.Products.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Name.ToLower() == lower, ct);
        if (existing is not null)
        {
            existing.Price = price;
            return (existing.Name, price, true);
        }

        _db.Products.Add(new Product { SellerId = seller.Id, Name = name, Price = price });
        await _db.SaveChangesAsync(ct);
        return (name, price, false);
    }

    private async Task HandleEditProductAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.SellerId == seller.Id && p.Name == cmd.Text, ct);
        if (product is null)
        {
            await ReplyAsync(seller, $"\"{cmd.Text}\" catalog mein nahi mila.", ct);
            return;
        }

        var oldPrice = product.Price;
        product.Price = cmd.Amount!.Value;
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id,
            ActionType = ActionType.ProductPriceChanged,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { ProductId = product.Id, OldPrice = oldPrice })
        });

        await ReplyAsync(seller, $"✅ {product.Name} price updated: {Formatters.Money(oldPrice)} → {Formatters.Money(product.Price)}", ct);
    }

    private async Task HandleMarkStatusAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var order = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == cmd.Number, ct);
        if (order is null)
        {
            await ReplyAsync(seller, $"Order #{cmd.Number} nahi mila.", ct);
            return;
        }

        if (cmd.Text == "paid")
        {
            await MarkOrderPaidAsync(seller, order, ct);
            return;
        }

        await ApplyStatusChangeAsync(seller, session, ctx, order, cmd.Text!, ct);
    }

    private async Task ApplyStatusChangeAsync(Seller seller, ConversationSession session, SessionContextData ctx, Order order, string statusKeyword, CancellationToken ct)
    {
        var previousStatus = order.Status;
        var newStatus = statusKeyword switch
        {
            "shipped" => OrderStatus.Shipped,
            "delivered" => OrderStatus.Delivered,
            "pending" => OrderStatus.Pending,
            _ => order.Status
        };

        order.Status = newStatus;
        order.ShippedAt = newStatus == OrderStatus.Shipped ? DateTime.UtcNow : order.ShippedAt;
        order.DeliveredAt = newStatus == OrderStatus.Delivered ? DateTime.UtcNow : order.DeliveredAt;

        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id,
            ActionType = ActionType.OrderStatusChanged,
            OrderId = order.Id,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { PreviousStatus = previousStatus.ToString() })
        });

        var reply = $"✅ Order #{order.Id} ({order.Customer?.Name} - {Formatters.ItemsSummary(order)}) marked as {Formatters.Status(newStatus)}.";
        if (newStatus == OrderStatus.Shipped && string.IsNullOrEmpty(order.TrackingNumber))
            reply += "\nTracking number add karna hai? (\"add tracking: courier, number\")";

        var askCodCollected = newStatus == OrderStatus.Delivered && order.PaymentMethod == OrderPaymentMethod.Cod && order.PaymentStatus == PaymentStatus.Unpaid;
        if (askCodCollected)
        {
            reply += "\nCash collect ho gaya?";
            ctx.CodCollectedOrderId = order.Id;
            SetState(session, ConversationState.AwaitingCodCollectedConfirmation);
        }

        await ReplyAsync(seller, reply, ct);
    }

    private async Task MarkOrderPaidAsync(Seller seller, Order order, CancellationToken ct)
    {
        order.PaymentStatus = PaymentStatus.Paid;
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id,
            ActionType = ActionType.OrderStatusChanged,
            OrderId = order.Id,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { PreviousPaymentStatus = PaymentStatus.Unpaid.ToString() })
        });
        await ReplyAsync(seller, $"✅ Order #{order.Id} marked as PAID.", ct);
        await CheckLoyaltyThresholdAsync(seller, order.CustomerId, ct);
    }

    private async Task HandleUnpaidOrdersAsync(Seller seller, CancellationToken ct)
    {
        var orders = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.PaymentStatus == PaymentStatus.Unpaid && o.Status != OrderStatus.Cancelled)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct);

        if (orders.Count == 0)
        {
            await ReplyAsync(seller, "Koi unpaid order nahi hai.", ct);
            return;
        }

        var lines = orders.Select((o, i) => Formatters.OrderLine(i + 1, o) + ", unpaid");
        await ReplyAsync(seller,
            $"💸 Unpaid Orders ({orders.Count}):\n\n{string.Join("\n", lines)}\n\n" +
            $"Total pending: {Formatters.Money(orders.Sum(o => o.Total))}\n\n" +
            "\"payment link 1\" likh kar link bhej saktay hain.", ct);
    }
}
