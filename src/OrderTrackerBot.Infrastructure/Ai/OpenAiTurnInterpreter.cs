using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Ai;

namespace OrderTrackerBot.Infrastructure.Ai;

/// <summary>
/// One OpenAI call that turns a message (typed or a voice transcript) into a <see cref="TurnAction"/>: a closed action plus typed fields.
/// Strict JSON schema, so the reply is always parseable. Any failure returns null and the caller keeps the older pipeline.
/// </summary>
public sealed class OpenAiTurnInterpreter : ITurnInterpreter
{
    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiTurnInterpreter> _logger;

    public OpenAiTurnInterpreter(HttpClient http, IOptions<OpenAiOptions> options, ILogger<OpenAiTurnInterpreter> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    private const string Prompt =
        "You are the understanding step of a WhatsApp shop bot. The user is a small shop owner in Pakistan. Many owners cannot read or " +
        "write well, speak Urdu with a Punjabi accent, and never type English commands. Their text or voice may be misheard, unpunctuated, " +
        "phonetically spelled, in any word order, or mixed with English words. Understand the MEANING, not exact words. Pick exactly one action:\n" +
        "- create_order: they are recording a customer's purchase (customer name, items with quantities, phone, address, delivery charge). " +
        "A customer name with a product and a quantity is an order even with no verb ('kurti 2 Sara'). " +
        "When the count is whole packs ('2 carton tel', 'ek peti'), set in_packs true, quantity = the number of packs, and unit_price = the price of ONE pack if said.\n" +
        "- update_status: an order that ALREADY exists was shipped, delivered, returned or put back to pending ('order 2 bhej diya', 'Ali ka order nikal gaya', " +
        "'teen number pohnch gaya'); give order_id or customer_name and status. Only when NO product or quantity is named. A message that names a PRODUCT is NEVER this: " +
        "'<customer> ko <number> <product> bhej do / bhej dein / bhejni hain / likh do / chahiye' and 'Ali ka order, do shawl ...' are NEW orders (create_order), even with the word bhej. " +
        "The number before the product is a quantity, never an order number.\n" +
        "- cancel_order: cancel an order; give order_id or customer_name.\n" +
        "- show_orders: list orders for a period, or pending ones. show_order: details of one order; give order_id.\n" +
        "- show_summary: sales / money summary (hisaab, kitni sale hui) for a period.\n" +
        "- add_product: they are saying what they sell (name, price, optional cost and stock; 'naya product', 'nayi chiz', 'catalog mein daal do', 'list mein shamil'). " +
        "A product name with its price and no buyer is add_product, never create_order. Put each product in products. price, cost and stock are for ONE piece. " +
        "When the product comes in a bigger pack (carton, box, peti, dozen, bori), also fill: pack_name; pack_qty = pieces in one pack; " +
        "pack_cost = what the owner PAYS / BUYS one whole pack for (words like 'aata hai', 'aati hai', 'milta hai', 'khareedta hun', 'ka hai', 'cost'); " +
        "pack_price = what the owner SELLS one whole pack for (words like 'bechta hun', 'bechna hai', 'mein deta hun', 'sale'); stock_packs = how many whole packs they have. " +
        "Each only when said; pack_cost and pack_price are different things and are never copied from each other. NEVER divide or multiply yourself: cost stays null when only " +
        "a pack's cost was said. When a range is said, use its HIGHER number for that field. Example: 'tel carton mein aata hai, carton 1000 ka hai, us mein 10 piece, " +
        "3 carton hain, piece 130 se 140 mein bechna hai, carton 1150 se 1200 mein' = price 140, cost null, stock null, pack_name carton, pack_qty 10, pack_cost 1000, " +
        "pack_price 1200, stock_packs 3.\n" +
        "- add_stock: more stock of an EXISTING product arrived or is being counted ('tel ke 2 carton aur aaye', 'kurti 5 aur aa gayi'). Put the product in products " +
        "with stock (pieces) or stock_packs (whole packs), and nothing else.\n" +
        "- edit_product: an existing product's price changes; put its name and the NEW price in products.\n" +
        "- show_catalog: show their products.\n" +
        "- add_customer: save a new buyer WITHOUT an order (customer.name, and phone / address / city when said). If the message also names items or a quantity, " +
        "it is create_order, never add_customer.\n" +
        "- edit_customer: one detail of a saved customer changes; customer_name, field (phone, address, city or name) and value (the new value).\n" +
        "- show_customer: they want a saved customer's details (phone number, address, their orders); customer_name, or null when no name was said (then all customers are listed).\n" +
        "- how_to: they ask HOW to do something ('kaise', 'tarika', 'how do I'). Set topic to the action they ask about. NEVER perform the action itself.\n" +
        "- contact_support: ONLY when they ask to TALK to a person, the support team or the makers of this bot ('kisi insaan se baat karni hai', 'support se baat karao'). " +
        "Asking for a 'contact number', 'phone number' or 'number' is about a CUSTOMER's number, never this: use show_customer when a customer is named, " +
        "otherwise clarify and ask which customer's number they want.\n" +
        "- edit_order: change the items of an order that ALREADY exists ('order 3 mein kurti ki quantity 2 kar do', 'Ali ke order se shirt nikal do', " +
        "'Sara ke order mein lawn suit ka rate 3000'). Give order_id (or customer_name) and edit_lines: one line per change, op set_quantity (item, quantity), " +
        "set_price (item, price), remove (item), or add (item = product name, quantity). A new order is create_order, never edit_order.\n" +
        "- undo: reverse the bot's last action.\n" +
        "- clarify: the message is about customers, orders or products but a needed detail is missing or it could mean two things. Put ONE short question in " +
        "question, in simple Roman Urdu (e.g. 'Kis customer ka number chahiye?').\n" +
        "- help_or_chat: a greeting, small talk, or anything not about the shop.\n" +
        "- out_of_scope: a shop task this bot does not do yet (expenses, discounts, receipts, profit reports). A PAYMENT or advance ('advance 500', 'payment mark karo', " +
        "'1000 de diye', 'paise mil gaye', 'ایڈوانس') is out_of_scope: never update_status for it.\n" +
        "Never invent a number, name or phone that is not in the text. Use null for anything not said. " +
        "order_id is ONLY the order number the owner said ('order 3', 'teen number wala', 'تین نمبر آرڈر' = 3); when they named a customer and no number, " +
        "leave order_id null and give customer_name. Never pick an order_id from the list of latest orders yourself. " +
        "Amounts spoken in words (Urdu, Roman Urdu or English) become digits. " +
        "The shop's products listed below are the likely ones; match item names to them by sound when the owner misspells or misspeaks them, " +
        "and copy the owner's own item name in product_name. Use a saved customer's name as it is listed when the owner clearly means that customer. " +
        "Periods: today, yesterday, this_week, last_week, this_month, last_month, last_7_days, last_30_days.\n" +
        "SEVERAL REQUESTS: one message may ask for more than one thing ('Ali ka order bhej do aur Sara ka phone 0300...'). Return each one as its own entry in " +
        "actions, in the order the owner said them, up to 5. A single request is one entry. Every entry is complete on its own: its own action, order_id, customer and fields.";

    public async Task<TurnAction?> UnderstandAsync(TurnContext context, string text, AiImageInput? image = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || (string.IsNullOrWhiteSpace(text) && image is null)) return null;

        var catalog = context.Catalog.Count == 0 ? "(no products yet)"
            : string.Join("\n", context.Catalog.Select(c => $"- {c.Name}: Rs.{c.Price}"));
        var system = Prompt +
            $"\n\nShop: {context.BusinessName}. Shop type: {context.ShopType ?? "not set"}. It has {context.CatalogSize} products in all; the list below is the likely part." +
            $"\nLikely products:\n{catalog}" +
            (context.KnownCustomers.Count > 0 ? "\nCustomers: " + string.Join("; ", context.KnownCustomers) : "") +
            (context.RecentOrders.Count > 0 ? "\nLatest orders:\n" + string.Join("\n", context.RecentOrders) : "") +
            (context.RecentExchanges.Count > 0 ? "\nRecent chat:\n" + string.Join("\n", context.RecentExchanges) : "") +
            (context.FromVoice ? "\nThe text is a voice note transcript." : "");

        try
        {
            var body = new JsonObject
            {
                ["model"] = _options.Model,
                ["temperature"] = 0.0,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = UserContent(text, image) }
                },
                ["response_format"] = Schema()
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions") { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
            var content = payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(content) ? null : Parse(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Turn interpreter failed; the older pipeline answers.");
            return null;
        }
    }

    /// <summary>Plain text, or text plus the picture when there is one (a screenshot of an order or a payment receipt).</summary>
    private static JsonNode UserContent(string text, AiImageInput? image)
    {
        if (image is null) return JsonValue.Create(text)!;
        var dataUrl = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Bytes)}";
        return new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = string.IsNullOrWhiteSpace(text) ? "The owner sent this picture." : text },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } }
        };
    }

    /// <summary>Parses the model's JSON. Returns null when the action is not in the closed list.</summary>
    internal static TurnAction? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // The reply lists every action the message asked for; the first is returned, the rest hang off it in order (TurnAction.Also).
        if (root.TryGetProperty("actions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var parsed = list.EnumerateArray().Select(ParseOne).OfType<TurnAction>().Take(TurnAction.MaxActionsPerMessage).ToList();
            if (parsed.Count == 0) return null;
            parsed[0].Also.AddRange(parsed.Skip(1));
            return parsed[0];
        }
        return ParseOne(root);
    }

    /// <summary>One action object (the fields of the closed list), or null when its action is not in the list.</summary>
    internal static TurnAction? ParseOne(JsonElement root)
    {
        var action = Str(root, "action");
        if (action is null || !TurnAction.All.Contains(action)) return null;

        var orders = new List<AiOrderDraft>();
        if (root.TryGetProperty("orders", out var ordersEl) && ordersEl.ValueKind == JsonValueKind.Array)
            foreach (var o in ordersEl.EnumerateArray())
            {
                var items = new List<AiOrderItemDraft>();
                if (o.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                    foreach (var i in itemsEl.EnumerateArray())
                    {
                        var name = Str(i, "product_name");
                        if (name is null) continue;
                        var qty = i.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt32(out var qv) && qv > 0 ? qv : 1;
                        items.Add(new AiOrderItemDraft
                        {
                            ProductName = name, Quantity = qty, StatedUnitPrice = Dec(i, "unit_price"),
                            InPacks = i.TryGetProperty("in_packs", out var inPacks) && inPacks.ValueKind == JsonValueKind.True,
                        });
                    }
                orders.Add(new AiOrderDraft
                {
                    CustomerName = Str(o, "customer_name"),
                    Phone = Str(o, "phone"),
                    Address = Str(o, "address"),
                    PaymentMethod = Str(o, "payment_method"),
                    DeliveryCharge = Dec(o, "delivery_charge"),
                    Items = items,
                });
            }

        var products = new List<AiNewProduct>();
        if (root.TryGetProperty("products", out var prodEl) && prodEl.ValueKind == JsonValueKind.Array)
            foreach (var p in prodEl.EnumerateArray())
            {
                var name = Str(p, "name");
                if (name is null) continue;
                products.Add(new AiNewProduct
                {
                    Name = name,
                    Price = Dec(p, "price"),
                    Cost = Dec(p, "cost"),
                    Stock = Int(p, "stock"),
                    PackName = Str(p, "pack_name"),
                    PackQty = Int(p, "pack_qty"),
                    PackCost = Dec(p, "pack_cost"),
                    PackPrice = Dec(p, "pack_price"),
                    StockPacks = Int(p, "stock_packs"),
                });
            }

        var orderEdits = new List<AiOrderEdit>();
        if (root.TryGetProperty("edit_lines", out var editEl) && editEl.ValueKind == JsonValueKind.Array)
            foreach (var e in editEl.EnumerateArray())
            {
                var op = Str(e, "op");
                if (op is null || !TurnAction.OrderEditOps.Contains(op)) continue;
                orderEdits.Add(new AiOrderEdit { Op = op, Item = Str(e, "item"), Quantity = Int(e, "quantity"), Price = Dec(e, "price") });
            }

        int? orderId = root.TryGetProperty("order_id", out var oid) && oid.ValueKind == JsonValueKind.Number && oid.TryGetInt32(out var idv) && idv > 0 ? idv : null;
        var status = Str(root, "status");
        var period = Str(root, "period");
        var field = Str(root, "field");
        var topic = Str(root, "topic");
        AiNewCustomer? customer = null;
        if (root.TryGetProperty("customer", out var cust) && cust.ValueKind == JsonValueKind.Object && Str(cust, "name") is { } customerName)
            customer = new AiNewCustomer { Name = customerName, Phone = Str(cust, "phone"), Address = Str(cust, "address"), City = Str(cust, "city") };

        // The model fills fields that do not belong to its action (a customer block on a "show" action, an order on a status change).
        // Only the fields of the chosen action are kept, so nothing is run or checked from a stray one.
        if (action != TurnAction.CreateOrder) orders.Clear();
        if (action is not (TurnAction.AddProduct or TurnAction.EditProduct or TurnAction.AddStock)) products.Clear();
        if (action != TurnAction.AddCustomer) customer = null;
        if (action != TurnAction.EditOrder) orderEdits.Clear();
        // Stock that arrived is a product's name and a count; a price the model copied from the catalog is not part of it.
        if (action == TurnAction.AddStock)
            products = products.Select(p => new AiNewProduct { Name = p.Name, Stock = p.Stock, StockPacks = p.StockPacks }).ToList();

        return new TurnAction
        {
            Action = action,
            Orders = orders,
            Products = products,
            OrderId = orderId,
            CustomerName = Str(root, "customer_name"),
            Status = status is not null && TurnAction.Statuses.Contains(status) ? status : null,
            Period = period is not null && TurnAction.Periods.Contains(period) ? period : null,
            PendingOnly = root.TryGetProperty("pending_only", out var po) && po.ValueKind == JsonValueKind.True,
            Customer = customer,
            Field = field is not null && TurnAction.CustomerFields.Contains(field) ? field : null,
            Value = Str(root, "value"),
            Topic = topic is not null && TurnAction.HowToTopics.Contains(topic) ? topic : null,
            Question = Str(root, "question"),
            OrderEdits = orderEdits,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d >= 0 ? d : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 0 ? n : null;

    private static JsonObject NullableString() => new() { ["type"] = new JsonArray { "string", "null" } };
    private static JsonObject NullableNumber() => new() { ["type"] = new JsonArray { "number", "null" } };

    private static JsonObject NullableEnum(IReadOnlyList<string> values) => new()
    {
        ["type"] = new JsonArray { "string", "null" },
        ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).Append(null).ToArray()),
    };

    private static JsonObject Schema()
    {
        var item = Obj(new JsonObject
        {
            ["product_name"] = NullableString(),
            ["quantity"] = NullableNumber(),
            ["unit_price"] = NullableNumber(),
            ["in_packs"] = new JsonObject { ["type"] = "boolean" },
        });
        var order = Obj(new JsonObject
        {
            ["customer_name"] = NullableString(),
            ["phone"] = NullableString(),
            ["address"] = NullableString(),
            ["payment_method"] = NullableString(),
            ["delivery_charge"] = NullableNumber(),
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = item },
        });
        var product = Obj(new JsonObject
        {
            ["name"] = NullableString(),
            ["price"] = NullableNumber(),
            ["cost"] = NullableNumber(),
            ["stock"] = NullableNumber(),
            ["pack_name"] = NullableString(),
            ["pack_qty"] = NullableNumber(),
            ["pack_cost"] = NullableNumber(),
            ["pack_price"] = NullableNumber(),
            ["stock_packs"] = NullableNumber(),
        });
        var editLine = Obj(new JsonObject
        {
            ["op"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(TurnAction.OrderEditOps.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()) },
            ["item"] = NullableString(),
            ["quantity"] = NullableNumber(),
            ["price"] = NullableNumber(),
        });
        var actionSchema = Obj(new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(TurnAction.All.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()) },
            ["orders"] = new JsonObject { ["type"] = "array", ["items"] = order },
            ["products"] = new JsonObject { ["type"] = "array", ["items"] = product },
            ["order_id"] = NullableNumber(),
            ["customer_name"] = NullableString(),
            ["status"] = NullableEnum(TurnAction.Statuses),
            ["period"] = NullableEnum(TurnAction.Periods),
            ["pending_only"] = new JsonObject { ["type"] = "boolean" },
            ["customer"] = Obj(new JsonObject
            {
                ["name"] = NullableString(),
                ["phone"] = NullableString(),
                ["address"] = NullableString(),
                ["city"] = NullableString(),
            }),
            ["field"] = NullableEnum(TurnAction.CustomerFields),
            ["value"] = NullableString(),
            ["topic"] = NullableEnum(TurnAction.HowToTopics),
            ["question"] = NullableString(),
            ["edit_lines"] = new JsonObject { ["type"] = "array", ["items"] = editLine },
        });
        var root = Obj(new JsonObject
        {
            ["actions"] = new JsonObject { ["type"] = "array", ["items"] = actionSchema },
        });
        return new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject { ["name"] = "turn_actions", ["strict"] = true, ["schema"] = root },
        };
    }

    /// <summary>The strict reply schema, for tests that check it describes what the parser reads.</summary>
    internal static JsonObject BuildSchemaForTests() => Schema();

    private static JsonObject Obj(JsonObject properties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()),
        ["additionalProperties"] = false,
    };
}
