namespace OrderTrackerBot.Application.Intents;

/// <summary>
/// Turns one of the five core intents (core-intents.json) and its slots into the typed command the deterministic parser already reads.
/// A combination with no command yet returns <see cref="CoreResolution.Gap"/>, which says so; it never guesses a number.
/// </summary>
public static class CoreIntentResolver
{
    public sealed record CoreResolution(string? Command, string? Gap);

    public static CoreResolution Resolve(string coreIntent, IReadOnlyDictionary<string, string> slots)
    {
        string S(string name) => slots.TryGetValue(name, out var v) ? v.Trim() : "";
        return coreIntent switch
        {
            "ORDER_QUERY" => OrderQuery(S("status"), S("period"), S("order"), S("customer")),
            "CATALOG_MANAGE" => CatalogManage(S("action"), S("product"), S("price"), S("cost"), S("stock"), S("category"), S("query"), S("group"), S("tier_price")),
            "BUSINESS_ANALYTICS" => BusinessAnalytics(S("metric"), S("period"), S("group_by"), S("product"), S("percent")),
            "STOCK" => Stock(S("action"), S("product"), S("qty")),
            "CUSTOMER" => Customer(S("action"), S("customer"), S("id"), S("field"), S("value")),
            _ => new CoreResolution(null, $"Unknown core intent {coreIntent}."),
        };
    }

    private static CoreResolution OrderQuery(string status, string period, string order, string customer)
    {
        if (order.Length > 0) return Ok($"order {order}");
        if (customer.Length > 0) return Ok($"{customer} ka order");
        if (status.Length > 0 && period.Length > 0) return Gap("Status aur period ek saath filter nahi ho sakte abhi.");
        return status switch
        {
            "pending" => Ok("pending orders"),
            "unpaid" => Ok("unpaid orders"),
            "cod" => Ok("cod pending"),
            _ => Ok(period.Length > 0 ? $"orders {period}" : "orders today"),
        };
    }

    private static CoreResolution CatalogManage(string action, string product, string price, string cost, string stock, string category, string query, string group, string tierPrice)
    {
        switch (action)
        {
            case "show": return Ok("catalog");
            case "share": return Ok("share catalog");
            case "search": return query.Length > 0 ? Ok($"catalog search {query}") : Gap("Kaun sa product dhoondna hai?");
            case "create":
                if (product.Length == 0 || price.Length == 0) return Gap("Naam aur price dono chahiye.");
                var line = $"{product} - {price}";
                if (cost.Length > 0) line += $", cost {cost}";
                if (stock.Length > 0) line += $", stock {stock}";
                if (category.Length > 0) line += $", category: {category}";
                return Ok(line);
            case "edit_price":
                return product.Length > 0 && price.Length > 0 ? Ok($"edit product: {product} - {price}") : Gap("Product aur naya price chahiye.");
            case "delete":
                return product.Length > 0 ? Ok($"delete product: {product}") : Gap("Kaun sa product hatana hai?");
            case "price_group":
                return product.Length > 0 && price.Length > 0 && group.Length > 0 && tierPrice.Length > 0
                    ? Ok($"{product} - {price}, price {group} {tierPrice}")
                    : Gap("Product, regular price, group aur group ka price chahiye.");
            default: return Gap("Catalog ka kaunsa kaam?");
        }
    }

    private static CoreResolution BusinessAnalytics(string metric, string period, string groupBy, string product, string percent)
    {
        if (groupBy is "city" or "courier" or "supplier") return Gap($"{groupBy} ke hisaab se {metric} abhi nahi nikalta.");
        if (product.Length > 0 && percent.Length > 0) return Ok($"{product} par {percent}% discount do to margin kitna bachega");
        return metric switch
        {
            "sales" when groupBy == "category" => Ok(period.Length > 0 ? $"sales by category {period}" : "sales by category"),
            "sales" => period switch
            {
                "yesterday" => Ok("kal ka hisaab"),
                "" or "today" => Ok("aaj ka hisaab"),
                _ => Gap("Is period ka sales summary abhi nahi hai."),
            },
            "profit" => Ok(period.Length > 0 ? $"profit {period}" : "profit"),
            "margin" => Ok(period.Length > 0 ? $"margin {period}" : "margin list"),
            "net" => Ok("monthly net"),
            "expense" => Ok(period.Length > 0 ? $"expenses {period}" : "expenses"),
            "top_seller" => Ok("kaun sa product sab se zyada bika"),
            "slow_seller" => Ok("slow movers"),
            _ => Gap("Kaun sa business number chahiye?"),
        };
    }

    private static CoreResolution Stock(string action, string product, string qty)
    {
        switch (action)
        {
            case "show": return Ok("stock");
            case "low": return Ok("low stock");
            case "restock": return Ok("restock list");
            case "set": return product.Length > 0 && qty.Length > 0 ? Ok($"stock {product} {qty}") : Gap("Product aur quantity chahiye.");
            case "add": return product.Length > 0 && qty.Length > 0 ? Ok($"stock {product} +{qty}") : Gap("Product aur quantity chahiye.");
            case "untrack": return product.Length > 0 ? Ok($"stock {product} off") : Gap("Kaun sa product?");
            default: return Gap("Stock ka kaunsa kaam?");
        }
    }

    private static CoreResolution Customer(string action, string customer, string id, string field, string value)
    {
        switch (action)
        {
            case "list": return Ok("customers");
            case "detail": return id.Length > 0 ? Ok($"customer {id}") : Gap("Customer ka number chahiye.");
            case "search": return customer.Length > 0 ? Ok($"search customer {customer}") : Gap("Kaun sa customer?");
            case "add": return customer.Length > 0 ? Ok($"customer add {customer}") : Gap("Customer ka naam chahiye.");
            case "delete": return customer.Length > 0 ? Ok($"delete customer {customer}") : Gap("Kaun sa customer?");
            case "restore": return customer.Length > 0 ? Ok($"restore customer {customer}") : Gap("Kaun sa customer?");
            case "balance": return customer.Length > 0 ? Ok($"{customer} ka kitna udhaar hai") : Gap("Kaun sa customer?");
            case "update":
                if (customer.Length == 0 || value.Length == 0) return Gap("Customer aur nayi value chahiye.");
                return field switch
                {
                    "phone" => Ok($"{customer} ka phone {value}"),
                    "address" => Ok($"{customer} ka address {value}"),
                    "city" => Ok($"{customer} ki city {value}"),
                    "email" => Ok($"{customer} ka email {value}"),
                    "group" => Ok($"{customer} ka group {value}"),
                    _ => Gap("Kaun si detail badalni hai?"),
                };
            default: return Gap("Customer ka kaunsa kaam?");
        }
    }

    private static CoreResolution Ok(string command) => new(command, null);
    private static CoreResolution Gap(string reason) => new(null, reason);
}
