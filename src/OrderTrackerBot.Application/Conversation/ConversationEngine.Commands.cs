using OrderTrackerBot.Application.Time;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    // Screen 10: the full command list (the 9 core commands are what a seller memorizes; the rest lives under "menu").
    private const string HelpText =
        "🆘 Yeh commands try karein:\n" +
        "• \"guide\" — step-by-step seekhein\n" +
        "• \"new order: naam, product, phone, address\"\n" +
        "• \"orders today\" / \"pending orders\" / \"today's summary\"\n" +
        "• \"order 12\" — detail · \"edit order 12\" — badlein\n" +
        "• \"mark 12 shipped/delivered/returned/paid\"\n" +
        "• \"order 12 advance 500\" · \"delivery 200\"\n" +
        "• \"[naam] ka order\" / \"[naam] ka tracking\"\n" +
        "• \"add tracking: courier, number\"\n" +
        "• \"unpaid orders\" / \"cod pending\" / \"payment link\"\n" +
        "• \"catalog\" · \"Kurti - 1800\" · \"stock Kurti 20\"\n" +
        "• \"add product/customer/order (detailed)\" — form\n" +
        "• \"trending products\" / \"slow movers\" / \"[product] ka report\"\n" +
        "• \"loyal customers\" / \"create discount\"\n" +
        "• \"Sara ka phone 0300...\" — customer update\n" +
        "• \"receipt 12\" — PDF · \"export\" — Excel\n" +
        "• \"expense 500 packaging\" · \"monthly net\"\n" +
        "• \"support queries\" · \"change language\"\n" +
        "• \"undo\"\n" +
        "\n" +
        "🎤 Voice note ya 📷 screenshot bhi bhej saktay hain.\n" +
        "Ya \"menu\" likh kar categorized list dekhein.";

    // Screen 10b: categorized menu.
    private const string MenuText =
        "📋 Main Menu\n" +
        "\n" +
        "📖 \"guide\" — naya hain? Step-by-step seekhein\n" +
        "🌐 \"change language\" — Roman Urdu/English/اردو\n" +
        "\n" +
        "📦 Orders\n" +
        " • new order: [details]\n" +
        " • orders today / pending orders\n" +
        " • [customer] ka order\n" +
        "\n" +
        "📊 Reports\n" +
        " • today's summary\n" +
        " • monthly net / expenses\n" +
        " • trending products (custom dates bhi)\n" +
        " • slow movers\n" +
        " • [product] ka report\n" +
        " • discount performance\n" +
        " • loyal customers\n" +
        " • cod pending\n" +
        "\n" +
        "🛍️ Catalog\n" +
        " • catalog\n" +
        " • share catalog\n" +
        " • add/edit/delete product\n" +
        " • bara catalog? Google Sheet link bhi bhej saktay hain (naam, price columns)\n" +
        "\n" +
        "💰 Payments\n" +
        " • payment link\n" +
        " • mark [order] paid\n" +
        "\n" +
        "🎟️ Discounts\n" +
        " • create discount\n" +
        " • discount list\n" +
        "\n" +
        "⚙️ Business Setup\n" +
        " • update business info (naam, city, type, IG handle)\n" +
        " • add payment: jazzcash/easypaisa, number\n" +
        " • change language\n" +
        " • create loyalty rule\n" +
        "\n" +
        "Command type karein, ya neeche button se category chunein.";

    private static readonly MenuRow BackToMenu = new("menu", "⬅️ Main menu");

    private static readonly IReadOnlyList<MenuSection> HelpSections = new[]
    {
        new MenuSection("Core commands", new[]
        {
            new MenuRow("orders today", "Orders today"),
            new MenuRow("pending orders", "Pending orders"),
            new MenuRow("catalog", "Catalog"),
            new MenuRow("payment link", "Payment link"),
            new MenuRow("menu", "Menu (baaki sab)")
        })
    };

    private static readonly IReadOnlyList<MenuSection> MainMenuSections = new[]
    {
        new MenuSection("Categories", new[]
        {
            new MenuRow("menu orders", "📦 Orders"),
            new MenuRow("menu reports", "📊 Reports"),
            new MenuRow("menu catalog", "🛍️ Catalog"),
            new MenuRow("menu payments", "💰 Payments"),
            new MenuRow("menu discounts", "🎟️ Discounts"),
            new MenuRow("menu customers", "👥 Customers"),
            new MenuRow("menu settings", "⚙️ Settings"),
            new MenuRow("business setup", "⚙️ Business Setup"),
            new MenuRow("change language", "🌐 Change Language")
        })
    };

    // Rows that need typed details (add product, add discount, ...) are "how to" commands that show the exact format.
    // Undo is deliberately not tappable: an accidental tap would silently revert the last action.
    private static readonly IReadOnlyDictionary<string, (string Title, string Body, MenuRow[] Rows)> MenuCategories =
        new Dictionary<string, (string, string, MenuRow[])>
        {
            ["orders"] = ("📦 Orders", "📦 Orders\n • new order: [details]\n • order [n] — poori detail\n • edit order [n]\n • [customer] ka order\n • mark [n] shipped/delivered/returned\n • receipt [n] — PDF receipt", new[]
            {
                new MenuRow("orders today", "Orders today"), new MenuRow("pending orders", "Pending orders"),
                new MenuRow("unpaid orders", "Unpaid orders"), new MenuRow("cod pending", "COD pending"), new MenuRow("receipt", "Last order receipt (PDF)"), new MenuRow("edit order", "Last order edit"),
                new MenuRow("new order (detailed)", "New order (form)"), BackToMenu
            }),
            ["reports"] = ("📊 Reports", "📊 Reports\n Sales aur trends ek tap par.\n • [product] ka report — e.g. \"Lawn Suit ka report\"\n • kharcha likhein: \"expense 500 packaging\"", new[]
            {
                new MenuRow("today's summary", "Today's summary"), new MenuRow("trending products", "Trending products"),
                new MenuRow("slow movers", "Slow movers"), new MenuRow("loyal customers", "Loyal customers"),
                new MenuRow("customer feedback", "Customer feedback"), new MenuRow("weekly summary", "Weekly summary"),
                new MenuRow("discount performance", "Discount performance"),
                new MenuRow("monthly net", "Monthly net"), new MenuRow("expenses", "Expenses"), BackToMenu
            }),
            ["catalog"] = ("🛍️ Catalog", "🛍️ Catalog\n • naya product: Kurti - 1800\n • stock: \"stock Kurti 20\" / \"stock\"\n • weight/pack: Sugar 5 kg - 500\n • edit product: Kurti - 1900\n • delete product: Kurti\n • wholesale: Kaju - price tiers: 1kg=320, 10kg=300\n • ek saath kai products bhi bhej saktay hain", new[]
            {
                new MenuRow("catalog", "View catalog"), new MenuRow("share catalog", "Share catalog"),
                new MenuRow("add product", "Add product"), new MenuRow("add product (detailed)", "Add product (form)"), BackToMenu
            }),
            ["payments"] = ("💰 Payments", "💰 Payments\n • mark [order] paid\n • advance / thori payment: order [n] advance 500\n • delivery charge: delivery 200\n • add tracking: courier, number", new[]
            {
                new MenuRow("payment link", "Payment link"), new MenuRow("unpaid orders", "Unpaid orders"),
                new MenuRow("cod pending", "COD pending"), new MenuRow("add payment", "Add payment method"), BackToMenu
            }),
            ["discounts"] = ("🎟️ Discounts", "🎟️ Discounts & loyalty\n Code banayein, ya loyalty rule set karein.", new[]
            {
                new MenuRow("discount list", "Discount list"), new MenuRow("add discount", "Add discount"),
                new MenuRow("create loyalty", "Add loyalty rule"), new MenuRow("loyal customers", "Loyal customers"),
                new MenuRow("campaign status", "Campaign status"), BackToMenu
            }),
            ["customers"] = ("👥 Customers", "👥 Customers\n • \"customer 1\" ya naam likh kar detail\n • update: \"Sara ka phone 0300...\" / \"Sara ka address ...\"\n • \"search customer: naam/phone\"\n • \"delete customer naam\" / \"restore customer naam\"\n • promotion: \"sab customers ko batao: naya stock aaya\"\n • customer ka sawal: \"order kab aayega? — Bilal ne poocha\"\n • \"mark [n] resolved\"", new[]
            {
                new MenuRow("customer list", "Customer list"), new MenuRow("support queries", "Support queries"),
                new MenuRow("comment leads", "📷 Comment leads"), new MenuRow("connect instagram", "📷 Connect Instagram"),
                new MenuRow("add customer (detailed)", "Add customer (form)"), BackToMenu
            }),
            ["settings"] = ("⚙️ Settings", "⚙️ Settings\n • feedback: [aapka message] — hamein bot ke baare mein batayein", new[]
            {
                new MenuRow("update business info", "Business info"), new MenuRow("add payment", "Payment methods"),
                new MenuRow("subscribe", "Plan / subscribe"), new MenuRow("logo", "Receipt logo / banner"), new MenuRow("export", "Export (Excel)"), BackToMenu
            })
        };

    private Task HandleMenuCategoryAsync(Seller seller, string category, CancellationToken ct)
    {
        var (title, body, rows) = MenuCategories[category];
        return _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, body, "Options dekhein",
            new[] { new MenuSection(title, rows) }, ct);
    }

    private async Task ExecuteCommandAsync(Seller seller, ConversationSession session, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        if (IsBlockedByPlan(seller, cmd.Kind))
        {
            await SendProOnlyAsync(seller, ct);
            return;
        }

        switch (cmd.Kind)
        {
            case CommandKind.DeleteProduct:
                await HandleDeleteProductAsync(seller, cmd.Text!, ct);
                return;
            case CommandKind.PriceTiers:
                await HandlePriceTiersAsync(seller, cmd, ct);
                return;
            case CommandKind.DeleteCustomer:
                await HandleDeleteCustomerRequestAsync(seller, session, ctx, cmd, ct);
                return;
            case CommandKind.RestoreCustomer:
                await HandleRestoreCustomerAsync(seller, cmd.Text!, ct);
                return;
            case CommandKind.MoreCustomers:
                await HandleCustomerListAsync(seller, ctx, ct, ctx.CustomerListPage + 1);
                return;
            case CommandKind.CampaignStatus:
                await HandleCampaignStatusAsync(seller, ct);
                return;
            case CommandKind.SupportQueries:
                await HandleSupportQueriesListAsync(seller, session, ct);
                return;
            case CommandKind.ResolveSupportQuery:
                await HandleResolveSupportQueryAsync(seller, cmd.Number!.Value, ct);
                return;
            case CommandKind.ReplySupportQuery:
                await HandleReplySupportQueryAsync(seller, cmd, ct);
                return;
            case CommandKind.ForwardedQuery:
                await HandleForwardedQueryAsync(seller, session, ctx, cmd.Text, cmd.Text2!, ct);
                return;
            case CommandKind.ConnectInstagram:
                await HandleConnectInstagramAsync(seller, ct);
                return;
            case CommandKind.DisconnectInstagram:
                await HandleDisconnectInstagramAsync(seller, ct);
                return;
            case CommandKind.CustomerUpdate:
                await HandleCustomerUpdateAsync(seller, cmd, ct);
                return;
            case CommandKind.Stock:
                await HandleStockAsync(seller, cmd, ct);
                return;
            case CommandKind.Expense:
                await HandleExpenseAsync(seller, cmd, ct);
                return;
            case CommandKind.ExpenseList:
                await HandleExpenseListAsync(seller, cmd.Text, ct);
                return;
            case CommandKind.MonthlyNet:
                await HandleMonthlyNetAsync(seller, cmd.Text, ct);
                return;
            case CommandKind.OrderDetail:
                await HandleOrderDetailAsync(seller, ctx, cmd.Number!.Value, ct);
                return;
            case CommandKind.OrderPayment:
                await HandleOrderPaymentAsync(seller, ctx, cmd.Number!.Value, cmd.Amount!.Value, ct);
                return;
            case CommandKind.EditOrder:
                await StartOrderEditAsync(seller, session, ctx, cmd.Number, ct);
                return;
            case CommandKind.DeliveryCharge:
                await HandleDeliveryChargeAsync(seller, cmd.Amount, ct);
                return;
            case CommandKind.OrderDeliveryCharge:
                await HandleOrderDeliveryChargeAsync(seller, ctx, cmd.Number!.Value, cmd.Amount ?? 0, ct);
                return;
            case CommandKind.Shortcuts:
                await HandleShortcutsToggleAsync(seller, ctx, cmd.Text!, ct);
                return;
            case CommandKind.Export:
                await HandleExportAsync(seller, cmd.Export!, ct);
                return;
            case CommandKind.BrandingHelp:
                await HandleBrandingHelpAsync(seller, cmd.Text, ct);
                return;
            case CommandKind.RemoveBranding:
                await HandleRemoveBrandingAsync(seller, cmd.Text!, ct);
                return;
            case CommandKind.Receipt:
                await HandleReceiptAsync(seller, ctx, cmd, ct);
                return;
            case CommandKind.ProductReport:
                await HandleProductReportAsync(seller, cmd.Text!, ct);
                return;
            case CommandKind.DiscountPerformance:
                await HandleDiscountPerformanceAsync(seller, ct);
                return;
            case CommandKind.CommentLeads:
                await HandleCommentLeadsListAsync(seller, ct);
                return;
            case CommandKind.LeadAction:
                await HandleLeadActionAsync(seller, ctx, cmd, ct);
                return;
            case CommandKind.WeeklySummary:
                await SendWeeklySummaryAsync(seller, DateTime.UtcNow, ct);
                return;
            case CommandKind.UpdateBusinessInfo:
                SetState(session, ConversationState.AwaitingBusinessInfo);
                var currentSummary = BusinessInfoSummary(seller);
                await ReplyAsync(seller,
                    (currentSummary.Length > 0 ? $"📋 Abhi ka record: {currentSummary}\n\n" : "") + BusinessInfoPrompt, ct);
                return;
            case CommandKind.Subscribe:
                await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, $"💳 Plans:\n\n{PlansText}\n\nPlan chunein:", PlanButtons, ct);
                return;
            case CommandKind.ChangeLanguage:
                await StartChangeLanguageAsync(seller, session, ct);
                return;
            case CommandKind.BusinessSetup:
                await HandleBusinessSetupAsync(seller, ct);
                return;
            case CommandKind.PaymentMethodPrompt:
                await StartPaymentMethodInputAsync(seller, session, ct);
                return;
            case CommandKind.Guide:
                await StartGuideAsync(seller, session, ctx, cmd.Text, ct);
                return;
            case CommandKind.GuideLater:
                await ReplyAsync(seller, "Theek hai 👍 Jab bhi zaroorat ho, \"guide\" likh dein.", ct);
                return;
            case CommandKind.Greeting:
                await HandleGreetingAsync(seller, ct);
                return;
            case CommandKind.Help:
                await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, HelpText, "Commands dekhein", HelpSections, ct);
                return;
            case CommandKind.Menu:
                await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, MenuText, "Menu kholein", MainMenuSections, ct);
                return;
            case CommandKind.MenuCategory:
                await HandleMenuCategoryAsync(seller, cmd.Text!, ct);
                return;
            case CommandKind.CustomerList:
                await HandleCustomerListAsync(seller, ctx, ct);
                return;
            case CommandKind.CustomerDetail:
                await HandleCustomerDetailAsync(seller, ctx, cmd, ct);
                return;
            case CommandKind.CustomerSearch:
                await HandleCustomerSearchAsync(seller, cmd.Text!, ct);
                return;
            case CommandKind.OrdersToday:
                await HandleOrdersTodayAsync(seller, ctx, cmd.Text, ct);
                return;
            case CommandKind.PendingOrders:
                await HandlePendingOrdersAsync(seller, ctx, ct);
                return;
            case CommandKind.TodaysSummary:
                await HandleTodaysSummaryAsync(seller, cmd.Text, ct);
                return;
            case CommandKind.Catalog:
                await HandleCatalogAsync(seller, ct);
                return;
            case CommandKind.AddProduct:
                await HandleAddProductAsync(seller, cmd, ct);
                return;
            case CommandKind.NewOrderHelp:
                await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, NewOrderHelpText, "Options dekhein", NewOrderHelpSections, ct);
                return;
            case CommandKind.DetailedForm:
                await HandleDetailedFormRequestAsync(seller, cmd.Text!, ct);
                return;
            case CommandKind.ShareCatalog:
                await HandleShareCatalogAsync(seller, ct);
                return;
            case CommandKind.HowTo:
                if (cmd.Text == "discount") SetState(session, ConversationState.AwaitingDiscountDetails);
                await ReplyAsync(seller, HowToText(cmd.Text!), ct);
                return;
            case CommandKind.AddProductsBulk:
                await HandleAddProductsBulkAsync(seller, cmd, ct);
                return;
            case CommandKind.ImportCatalogSheet:
                await HandleImportCatalogSheetAsync(seller, cmd, ct);
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
                await HandlePaymentLinkAsync(seller,
                    new ParsedCommand { Kind = CommandKind.PaymentLink, Number = cmd.Number is { } n ? ResolveListNumber(ctx, n).OrderId : null }, ct);
                return;
            case CommandKind.AddPaymentMethod:
                await HandleAddPaymentMethodAsync(seller, cmd, ct);
                return;
            case CommandKind.UnpaidOrders:
                await HandleUnpaidOrdersAsync(seller, ctx, ct);
                return;
            case CommandKind.CodPending:
                ctx.RuntimeFilterCommand = "cod";
                SetState(session, ConversationState.AwaitingRuntimeFilterChoice);
                await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, "💵 Kitne purane pending dekhne hain?", new[] { "All", "3+ days", "7+ days" }, ct);
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
                await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, "📈 Konsi time period dekhna chahte hain? (\"last 30 days\" bhi likh saktay hain)",
                    new[] { "This Week", "This Month", "Custom Dates" }, ct);
                return;
            case CommandKind.SlowMovers:
                ctx.RuntimeFilterCommand = "slow";
                SetState(session, ConversationState.AwaitingRuntimeFilterChoice);
                await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, "📉 Kitne din se koi order nahi aaya?", new[] { "7 days", "14 days", "30 days" }, ct);
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
        var unpaid = await _db.Orders.CountAsync(o => o.SellerId == seller.Id && o.PaymentStatus == PaymentStatus.Unpaid && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned, ct);
        await ReplyAsync(seller, Formatters.ReturningGreeting(seller.PreferredLanguage, seller.BusinessName, pending, unpaid), ct);
    }

    // "mark 1 shipped" / "payment link 1" mean position 1 of the last numbered list shown; with no list (or a number
    // beyond it) the number is the real order id.
    private static (int OrderId, bool FromList) ResolveListNumber(SessionContextData ctx, int number) =>
        ctx.LastListOrderIds is { Count: > 0 } ids && number >= 1 && number <= ids.Count
            ? (ids[number - 1], true)
            : (number, false);

    /// <summary>The report window for "today" (null) / "yesterday" / "lastmonth" in the seller's local time.</summary>
    private static (DateTime StartUtc, DateTime EndUtc) ReportRange(Seller seller, string? period)
    {
        var now = DateTime.UtcNow;
        return period switch
        {
            "yesterday" => SellerClock.LocalDayRangeUtc(seller.TimeZoneId, now, -1),
            "lastmonth" => SellerClock.PreviousMonthRangeUtc(seller.TimeZoneId, now),
            _ => (SellerClock.StartOfLocalDayUtc(seller.TimeZoneId, now), DateTime.MaxValue)
        };
    }

    private async Task HandleOrdersTodayAsync(Seller seller, SessionContextData ctx, string? period, CancellationToken ct)
    {
        var (start, end) = ReportRange(seller, period);
        var orders = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct);

        ctx.LastListOrderIds = orders.Select(o => o.Id).ToList();
        await ReplyAsync(seller, Formatters.OrdersToday(seller.PreferredLanguage, orders, period), ct);
    }

    private async Task HandlePendingOrdersAsync(Seller seller, SessionContextData ctx, CancellationToken ct)
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

        ctx.LastListOrderIds = orders.Select(o => o.Id).ToList();
        var lines = orders.Select((o, i) => Formatters.OrderLine(i + 1, o));
        await ReplyAsync(seller, $"📦 Pending Orders ({orders.Count}):\n\n{string.Join("\n", lines)}\n\nReply \"mark 1 shipped\" to update.", ct);
    }

    private async Task HandleTodaysSummaryAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end) = ReportRange(seller, period);
        var label = period switch { "yesterday" => "Yesterday's", "lastmonth" => "Last Month's", _ => "Today's" };
        var orders = await _db.Orders
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .ToListAsync(ct);

        var delivered = orders.Count(o => o.Status == OrderStatus.Delivered);
        var shipped = orders.Count(o => o.Status == OrderStatus.Shipped);
        var pending = orders.Count(o => o.Status == OrderStatus.Pending);
        var cod = orders.Where(o => o.PaymentMethod == OrderPaymentMethod.Cod).Sum(OrderMoney.Received);
        var prepaid = orders.Where(o => o.PaymentMethod != OrderPaymentMethod.Cod).Sum(OrderMoney.Received);
        var totalSales = orders.Sum(o => o.Total);
        var returned = await _db.Orders.CountAsync(o => o.SellerId == seller.Id && o.Status == OrderStatus.Returned
            && o.ReturnedAt >= start && o.ReturnedAt < end, ct);

        await ReplyAsync(seller,
            $"🧾 {label} Reconciliation — {seller.BusinessName}\n\n" +
            $"Orders: {orders.Count}\n" +
            $"Delivered: {delivered} | Shipped: {shipped} | Pending: {pending}\n" +
            (returned > 0 ? $"Returned: {returned}\n" : "") +
            $"Cash collected (COD): {Formatters.Money(cod)}\n" +
            $"Prepaid received: {Formatters.Money(prepaid)}\n" +
            $"Total sales {(period is null ? "today" : label.Replace("'s", "").ToLowerInvariant())}: {Formatters.Money(totalSales)}\n\n" +
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
                "Lawn Suit - 3500\nKurti - 1800\nDupatta - 900\n\n" +
                "Weight/pack wale products: Sugar 5 kg - 500", ct);
            return;
        }

        var lines = products.Select((p, i) => $"{i + 1} {Formatters.ProductLabel(p)} - {Formatters.Money(p.Price)}" + (p.StockQty is { } q ? $" (stock {q})" : ""));
        await ReplyAsync(seller,
            $"🛍️ Aapka Catalog ({products.Count} products):\n\n{string.Join("\n", lines)}\n\n" +
            "Naya product add karne ke liye bas likhein: Kurti - 1800\n" +
            "Price badalne ke liye: edit product: Kurti - 1900", ct);
    }

    private async Task HandleShareCatalogAsync(Seller seller, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Id).ToListAsync(ct);
        if (products.Count == 0)
        {
            await ReplyAsync(seller, "Catalog abhi khali hai — pehle products add karein (e.g. Kurti - 1800).", ct);
            return;
        }

        var lines = products.Select((p, i) => $"{i + 1}. {Formatters.ProductLabel(p)} - {Formatters.Money(p.Price)}");
        await ReplyAsync(seller,
            $"📋 {seller.BusinessName} — Catalog\n\n{string.Join("\n", lines)}\n\n" +
            "Yeh copy kar ke customer ko bhej dein, ya screenshot le kar forward karein.", ct);
    }

    // "new order" typed on its own: show how, instead of sending an empty order to the AI.
    private const string NewOrderHelpText =
        "📦 Naya order darj karne ke 3 aasaan tareeqay:\n\n" +
        "1️⃣ Customer ka message yahan forward/paste kar dein\n" +
        "2️⃣ Ya ek line mein likhein:\nAyesha, 2 Lawn Suit, 03001234567, Lahore\n" +
        "3️⃣ Ya order ki screenshot bhej dein 📷\n\n" +
        "Main tafseel nikaal kar confirm karwa loon ga. Form se darj karna ho to neeche se chunein 👇";

    private static readonly IReadOnlyList<MenuSection> NewOrderHelpSections = new[]
    {
        new MenuSection("Order", new[]
        {
            new MenuRow("new order (detailed)", "📝 Form se order"),
            new MenuRow("catalog", "🛍️ Catalog dekhein"),
            new MenuRow("orders today", "📦 Aaj ke orders"),
            BackToMenu
        })
    };

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
        var line = cmd.Product ?? new ProductLine(cmd.Text!, cmd.Amount!.Value, "piece", 1);
        var (product, updated) = await UpsertProductAsync(seller, line, ct);
        var label = Formatters.ProductLabel(product);
        await ReplyAsync(seller,
            updated ? $"✅ {label} price updated: {Formatters.Money(product.Price)}{ProductDetailsText(product)}"
            : line.UnitType != "piece" || line.UnitQty != 1 ? $"✅ Added: {label} - {Formatters.Money(product.Price)}{ProductDetailsText(product)}"
            : $"✅ {label} - {Formatters.Money(product.Price)}{ProductDetailsText(product)} catalog mein add ho gaya.\n\nAur add karein (Naam - price), ya \"catalog\" likhein.", ct);
    }

    /// <summary>
    /// The seller described products they sell ("teen khaddar chadar aur do wool dupatte naye products hain") — not an order. Products that came
    /// with a price are saved; for the rest the bot asks for the price instead of guessing one.
    /// </summary>
    private async Task HandleNewProductsAsync(Seller seller, SessionContextData ctx, IReadOnlyList<AiNewProduct> products, string? sourceText, CancellationToken ct)
    {
        // The model must not make up numbers (it likes to copy one from a similar catalog product): a price, cost or stock only counts when the
        // seller wrote that number as digits, and an attribute only when its value appears in what they wrote.
        if (sourceText is not null)
        {
            var written = System.Text.RegularExpressions.Regex.Matches(sourceText.Replace(",", ""), @"\d+(?:\.\d+)?")
                .Select(m => decimal.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
            products = products.Select(p => new AiNewProduct
            {
                Name = p.Name,
                Price = p.Price is { } price && written.Contains(price) ? price : null,
                Cost = p.Cost is { } cost && written.Contains(cost) ? cost : null,
                Stock = p.Stock is { } stock && written.Contains(stock) ? stock : null,
                Attributes = p.Attributes.Where(a => sourceText.Contains(a.Value, StringComparison.OrdinalIgnoreCase)).ToDictionary(a => a.Key, a => a.Value)
            }).ToList();
        }

        var pendingExtras = ctx.PendingProductExtras ?? new Dictionary<string, ProductExtras>(StringComparer.OrdinalIgnoreCase);
        pendingExtras = new Dictionary<string, ProductExtras>(pendingExtras, StringComparer.OrdinalIgnoreCase);

        var saved = new List<string>();
        foreach (var p in products.Where(p => p.Price is not null))
        {
            pendingExtras.TryGetValue(p.Name, out var earlier);
            var (product, _) = await UpsertProductAsync(seller, new ProductLine(p.Name, p.Price!.Value, "piece", 1, MergeExtras(earlier, ExtrasOf(p))), ct);
            pendingExtras.Remove(p.Name);
            saved.Add($"• {Formatters.ProductLabel(product)} - {Formatters.Money(product.Price)}{ProductDetailsText(product)}");
        }

        foreach (var p in products.Where(p => p.Price is null && ExtrasOf(p) is not null))
        {
            pendingExtras.TryGetValue(p.Name, out var earlier);
            pendingExtras[p.Name] = MergeExtras(earlier, ExtrasOf(p))!;
        }

        var needPrice = products.Where(p => p.Price is null).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Remember who is still waiting for a price, so "teenon ki 5000" in the next message prices them instead of creating a product.
        var activeNames = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).Select(p => p.Name).ToListAsync(ct);
        var stillWaiting = (ctx.PendingPriceProducts ?? new List<string>()).Concat(needPrice)
            .Where(n => !activeNames.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ctx.PendingPriceProducts = stillWaiting.Count == 0 ? null : stillWaiting;
        ctx.PendingProductExtras = pendingExtras.Where(e => stillWaiting.Contains(e.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(e => e.Key, e => e.Value);
        if (ctx.PendingProductExtras.Count == 0) ctx.PendingProductExtras = null;
        needPrice = stillWaiting;

        var reply = saved.Count == 0 ? "" : $"✅ {saved.Count} product{(saved.Count == 1 ? "" : "s")} catalog mein add ho gaye:\n{string.Join("\n", saved)}\n\n";
        if (needPrice.Count > 0)
            reply += "📦 Samajh gaya — yeh naye products hain, order nahi:\n" +
                     string.Join("\n", needPrice.Select(n => $"• {n}{(ctx.PendingProductExtras is { } extras && extras.FirstOrDefault(e => string.Equals(e.Key, n, StringComparison.OrdinalIgnoreCase)) is { Value: not null } found ? ExtrasText(found.Value) : "")}")) +
                     "\n\nBas har product ki sale price bata dein (misaal: 'Kurti - 1800' ya \"teenon ki 5000\") — jo kuch aap ne bataya hai (cost, stock) woh yaad hai.";
        await ReplyAsync(seller, reply.TrimEnd(), ct);
    }

    /// <summary>Cost, stock and attributes the model read for one new product (color/size go to their own fields); null when there is none.</summary>
    private static ProductExtras? ExtrasOf(AiNewProduct p)
    {
        string? color = null, size = null;
        var other = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in p.Attributes)
        {
            if (name.Equals("color", StringComparison.OrdinalIgnoreCase) || name.Equals("colour", StringComparison.OrdinalIgnoreCase) || name.Equals("rang", StringComparison.OrdinalIgnoreCase)) color = value;
            else if (name.Equals("size", StringComparison.OrdinalIgnoreCase)) size = value;
            else other[name] = value;
        }
        return p.Cost is null && p.Stock is null && color is null && size is null && other.Count == 0
            ? null
            : new ProductExtras(p.Cost, p.Stock, null, size, color, null, other.Count == 0 ? null : other);
    }

    /// <summary>What was said later wins over what was said earlier.</summary>
    private static ProductExtras? MergeExtras(ProductExtras? earlier, ProductExtras? later)
    {
        if (earlier is null) return later;
        if (later is null) return earlier;
        var attributes = new Dictionary<string, string>(earlier.Attributes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in later.Attributes ?? new Dictionary<string, string>()) attributes[name] = value;
        return new ProductExtras(later.Cost ?? earlier.Cost, later.Stock ?? earlier.Stock, later.Category ?? earlier.Category, later.Size ?? earlier.Size,
            later.Color ?? earlier.Color, later.Sku ?? earlier.Sku, attributes.Count == 0 ? null : attributes);
    }

    private static string ExtrasText(ProductExtras e)
    {
        var parts = new List<string>();
        if (e.Cost is { } cost) parts.Add($"cost {Formatters.Money(cost)}");
        if (e.Stock is { } stock) parts.Add($"stock {stock}");
        if (e.Color is not null) parts.Add($"color: {e.Color}");
        if (e.Size is not null) parts.Add($"size: {e.Size}");
        if (e.Attributes is not null) parts.AddRange(e.Attributes.Select(a => $"{a.Key}: {a.Value}"));
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    private async Task HandleAddProductsBulkAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var results = new List<string>();
        foreach (var line in cmd.Text!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!CommandParser.TryParseProductLine(line, out ProductLine? parsed)) continue;
            var (product, updated) = await UpsertProductAsync(seller, parsed!, ct);
            results.Add($"{results.Count + 1} {Formatters.ProductLabel(product)} - {Formatters.Money(product.Price)}{ProductDetailsText(product)}{(updated ? " (updated)" : "")}");
        }
        await ReplyAsync(seller, $"✅ {results.Count} products save ho gaye:\n\n{string.Join("\n", results)}\n\n\"catalog\" likh kar poori list dekhein.", ct);
    }

    private async Task HandleImportCatalogSheetAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var lines = _catalogSheets is null ? null : await _catalogSheets.FetchProductLinesAsync(cmd.Text!, ct);
        if (lines is null)
        {
            await ReplyAsync(seller,
                "⚠️ Sheet se products load nahi ho sake. Check karein ke sheet \"Anyone with the link can view\" par set ho, ya products ek ek karke bhi bhej saktay hain (e.g. 'Kurti - 1800').", ct);
            return;
        }

        var results = new List<string>();
        foreach (var line in lines)
        {
            if (!CommandParser.TryParseProductLine(line, out ProductLine? parsed)) continue;
            var (product, updated) = await UpsertProductAsync(seller, parsed!, ct);
            results.Add($"{results.Count + 1} {Formatters.ProductLabel(product)} - {Formatters.Money(product.Price)}{ProductDetailsText(product)}{(updated ? " (updated)" : "")}");
        }

        if (results.Count == 0)
        {
            await ReplyAsync(seller,
                "Sheet parh li lekin koi valid product row nahi mila — har row mein pehla column naam, doosra column price hona chahiye.", ct);
            return;
        }

        await ReplyAsync(seller, $"✅ Sheet se {results.Count} products import ho gaye:\n\n{string.Join("\n", results)}\n\n\"catalog\" likh kar poori list dekhein.", ct);
    }

    // The same name with a different pack size ("Sugar 5kg" vs "Sugar 10kg") is a separate listing.
    private async Task<(Product Product, bool Updated)> UpsertProductAsync(Seller seller, ProductLine line, CancellationToken ct)
    {
        var lower = line.Name.ToLower();
        var existing = (await _db.Products.Where(p => p.SellerId == seller.Id && p.Name.ToLower() == lower).ToListAsync(ct))
            .FirstOrDefault(p => p.UnitType == line.UnitType && p.UnitQty == line.UnitQty);
        if (existing is not null)
        {
            existing.Price = line.Price;
            existing.IsActive = true;
            ApplyProductExtras(existing, line.Extras);
            return (existing, true);
        }

        var product = new Product { SellerId = seller.Id, Name = line.Name, Price = line.Price, UnitType = line.UnitType, UnitQty = line.UnitQty };
        ApplyProductExtras(product, line.Extras);
        _db.Products.Add(product);
        await _db.SaveChangesAsync(ct);
        return (product, false);
    }

    /// <summary>Cost, stock and attributes the seller wrote after the price; only what was written is changed.</summary>
    private static void ApplyProductExtras(Product product, ProductExtras? extras)
    {
        if (extras is null) return;
        if (extras.Cost is { } cost) product.CostPrice = cost;
        if (extras.Stock is { } stock) product.StockQty = stock;
        if (extras.Category is not null) product.Category = extras.Category;
        if (extras.Size is not null) product.Size = extras.Size;
        if (extras.Color is not null) product.Color = extras.Color;
        if (extras.Sku is not null) product.Sku = extras.Sku;
        if (extras.Attributes is { Count: > 0 } added)
        {
            var merged = ProductAttributes(product);
            foreach (var (name, value) in added) merged[name] = value;
            product.AttributesJson = System.Text.Json.JsonSerializer.Serialize(merged);
        }
    }

    private static Dictionary<string, string> ProductAttributes(Product product)
    {
        try
        {
            return string.IsNullOrWhiteSpace(product.AttributesJson)
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(product.AttributesJson) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>" (cost Rs.300, stock 10, fabric: cotton)" for the seller's confirmation; color/size are already in the product label. Seller-only text.</summary>
    private static string ProductDetailsText(Product product)
    {
        var parts = new List<string>();
        if (product.CostPrice is { } cost) parts.Add($"cost {Formatters.Money(cost)}");
        if (product.StockQty is { } stock) parts.Add($"stock {stock}");
        parts.AddRange(ProductAttributes(product).Select(a => $"{a.Key}: {a.Value}"));
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    private async Task HandleDeleteProductAsync(Seller seller, string name, CancellationToken ct)
    {
        var product = await FindProductAsync(seller, name, ct);
        if (product is null)
        {
            await ReplyAsync(seller, $"\"{name}\" catalog mein nahi mila. \"catalog\" likh kar list dekhein.", ct);
            return;
        }

        product.IsActive = false; // soft delete: old orders keep pointing at it
        await ReplyAsync(seller, $"✅ {Formatters.ProductLabel(product)} catalog se hata diya.", ct);
    }

    private async Task<Product?> FindProductAsync(Seller seller, string name, CancellationToken ct)
    {
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        var wanted = name.Trim().ToLower();
        return products.FirstOrDefault(p => Formatters.ProductLabel(p).ToLower() == wanted)
               ?? products.FirstOrDefault(p => p.Name.ToLower() == wanted);
    }

    private static readonly System.Text.RegularExpressions.Regex TierSpec =
        new(@"(\d+(?:\.\d+)?)\s*([a-z]*)\s*[=:]\s*(?:rs\.?\s*)?(\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Screen 7c-4: "Dakao Kaju - price tiers: 1kg=320, 10kg=300, 25kg=280".</summary>
    private async Task HandlePriceTiersAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var matches = TierSpec.Matches(cmd.Text2!);
        var tiers = matches.Select(m => (Min: decimal.Parse(m.Groups[1].Value), Unit: CommandParser.NormalizeUnit(m.Groups[2].Value), Price: decimal.Parse(m.Groups[3].Value)))
            .OrderBy(t => t.Min).ToList();
        if (tiers.Count == 0)
        {
            await ReplyAsync(seller, "Format: \"Kaju - price tiers: 1kg=320, 10kg=300, 25kg=280\"", ct);
            return;
        }

        var unit = tiers.Select(t => t.Unit).FirstOrDefault(u => u is not null) ?? "piece";
        var product = await FindProductAsync(seller, cmd.Text!, ct);
        if (product is null)
        {
            product = new Product { SellerId = seller.Id, Name = cmd.Text!, Price = tiers[0].Price, UnitType = unit, UnitQty = 1 };
            _db.Products.Add(product);
        }
        else
        {
            product.UnitType = unit;
            product.UnitQty = 1;
            product.Price = tiers[0].Price;
        }
        await _db.SaveChangesAsync(ct);

        _db.PriceTiers.RemoveRange(await _db.PriceTiers.Where(t => t.ProductId == product.Id).ToListAsync(ct));
        foreach (var t in tiers) _db.PriceTiers.Add(new PriceTier { ProductId = product.Id, MinQty = t.Min, PricePerUnit = t.Price });

        var u = Formatters.UnitShort(unit).Trim();
        var lines = tiers.Select((t, i) => i + 1 < tiers.Count
            ? $"{Formatters.Quantity(t.Min)}-{Formatters.Quantity(tiers[i + 1].Min - 1)} {u}: {Formatters.Money(t.Price)}/{u}"
            : $"{Formatters.Quantity(t.Min)}+ {u}: {Formatters.Money(t.Price)}/{u}");
        await ReplyAsync(seller, $"✅ {product.Name} — bulk pricing saved:\n{string.Join("\n", lines)}", ct);
    }

    private async Task HandleEditProductAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var product = await FindProductAsync(seller, cmd.Text!, ct);
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
        var (orderId, fromList) = ResolveListNumber(ctx, cmd.Number!.Value);
        var order = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
        if (order is null)
        {
            await ReplyAsync(seller, $"Order #{cmd.Number} nahi mila.", ct);
            return;
        }

        if (cmd.Text == "paid")
        {
            await MarkOrderPaidAsync(seller, order, ct);
        }
        else
        {
            await ApplyStatusChangeAsync(seller, session, ctx, order, cmd.Text!, ct);
        }

        if (fromList)
            await ReplyAsync(seller, $"ℹ️ \"{cmd.Number}\" aapki last list ka number tha (Order #{order.Id}). Naya number ke liye pehle list dobara dekhein.", ct);
    }

    private async Task ApplyStatusChangeAsync(Seller seller, ConversationSession session, SessionContextData ctx, Order order, string statusKeyword, CancellationToken ct)
    {
        var previousStatus = order.Status;
        var newStatus = statusKeyword switch
        {
            "shipped" => OrderStatus.Shipped,
            "delivered" => OrderStatus.Delivered,
            "pending" => OrderStatus.Pending,
            "returned" => OrderStatus.Returned,
            _ => order.Status
        };

        // Only something that left the shop can come back; a pending order is cancelled instead.
        if (newStatus == OrderStatus.Returned && previousStatus is not (OrderStatus.Shipped or OrderStatus.Delivered))
        {
            await ReplyAsync(seller, previousStatus == OrderStatus.Returned
                ? $"Order #{order.Id} pehle se RETURNED hai."
                : $"Order #{order.Id} abhi {Formatters.Status(previousStatus)} hai — bheja hi nahi gaya to wapas nahi aa sakta. Hatana ho to: \"cancel order {order.Id}\"", ct);
            return;
        }

        var stockBefore = StockFootprint(order);
        order.Status = newStatus;
        await ApplyStockChangeAsync(seller, stockBefore, StockFootprint(order), ct);
        order.ShippedAt = newStatus == OrderStatus.Shipped ? DateTime.UtcNow : order.ShippedAt;
        order.DeliveredAt = newStatus == OrderStatus.Delivered ? DateTime.UtcNow : order.DeliveredAt;
        order.ReturnedAt = newStatus == OrderStatus.Returned ? DateTime.UtcNow : order.ReturnedAt;

        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id,
            ActionType = ActionType.OrderStatusChanged,
            OrderId = order.Id,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { PreviousStatus = previousStatus.ToString() })
        });

        var reply = $"✅ Order #{order.Id} ({order.Customer?.Name} - {Formatters.ItemsSummary(order)}) marked as {Formatters.Status(newStatus)}.";
        if (newStatus == OrderStatus.Returned)
            reply += order.PaymentStatus == PaymentStatus.Paid
                ? "\nIs ki payment PAID thi — paise wapas kiye hon to yaad rakhein. Sales reports se yeh order nikal diya gaya hai."
                : "\nSales reports se yeh order nikal diya gaya hai.";
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
        MarkFullyPaid(seller, order);
        await ReplyAsync(seller, $"✅ Order #{order.Id} marked as PAID.", ct);
    }

    /// <summary>Marks the order fully paid (undoable) and returns what was still owed, i.e. what was just collected.</summary>
    private decimal MarkFullyPaid(Seller seller, Order order)
    {
        var collected = OrderMoney.Balance(order);
        LogPaymentChange(seller, order);
        order.PaymentStatus = PaymentStatus.Paid;
        order.PaidAt = DateTime.UtcNow;
        order.AmountPaid = order.Total;
        return collected;
    }

    /// <summary>Call before changing payment fields: undo restores the status and the amount paid so far.</summary>
    private void LogPaymentChange(Seller seller, Order order) =>
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id,
            ActionType = ActionType.OrderStatusChanged,
            OrderId = order.Id,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { PreviousPaymentStatus = order.PaymentStatus.ToString(), PreviousAmountPaid = order.AmountPaid })
        });

    private async Task HandleUnpaidOrdersAsync(Seller seller, SessionContextData ctx, CancellationToken ct)
    {
        var orders = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.PaymentStatus == PaymentStatus.Unpaid && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(ct);

        if (orders.Count == 0)
        {
            await ReplyAsync(seller, "Koi unpaid order nahi hai.", ct);
            return;
        }

        ctx.LastListOrderIds = orders.Select(o => o.Id).ToList();
        var lines = orders.Select((o, i) => Formatters.OrderLine(i + 1, o) + (o.AmountPaid > 0 ? $", baqi {Formatters.Money(OrderMoney.Balance(o))}" : ", unpaid"));
        await ReplyAsync(seller,
            $"💸 Unpaid Orders ({orders.Count}):\n\n{string.Join("\n", lines)}\n\n" +
            $"Total pending: {Formatters.Money(orders.Sum(OrderMoney.Balance))}\n\n" +
            "\"payment link 1\" likh kar link bhej saktay hain.", ct);
    }
}
