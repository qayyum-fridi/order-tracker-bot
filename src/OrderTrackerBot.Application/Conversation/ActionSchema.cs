using OrderTrackerBot.Application.Ai;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Stage 5b. Per-action schema: which fields are required, what each value must be, and which are checked when given.
/// <see cref="Missing"/> lists required fields the message does not supply; <see cref="Invalid"/> lists supplied values that break a rule.
/// Each label names one field, so the question asks only for that field; fields that are already right are never asked again.
/// Checks against the database (for example that a customer belongs to this seller) are not here: the reference resolver does them.
/// </summary>
public static class ActionSchema
{
    /// <summary>Required fields of the action that are absent. Labels are the words the question uses.</summary>
    public static IReadOnlyList<string> Missing(TurnAction? action)
    {
        if (action is null) return Array.Empty<string>();
        var missing = new List<string>();

        switch (action.Action)
        {
            case TurnAction.CreateOrder:
                if (action.Orders.Count == 0) { missing.Add("order details"); break; }
                // A walk-in order may have no customer name; only an order with no items is incomplete.
                if (action.Orders.Any(order => order.Items.Count == 0)) missing.Add("order items");
                break;

            // Labels for these cases are the ones the engine's pending-field flow already uses; do not rename them.
            case TurnAction.AddProduct when action.Products.Count == 0:
                missing.Add("product");
                break;

            case TurnAction.EditProduct when action.Products.Count == 0 || action.Products[0].Price is not > 0:
                missing.Add("product and price");
                break;

            case TurnAction.AddStock when action.Products.Count == 0 || action.Products[0].Stock is null:
                missing.Add("product and stock");
                break;

            case TurnAction.AddCustomer when action.Customer is null:
                missing.Add("customer");
                break;
            case TurnAction.AddCustomer when string.IsNullOrWhiteSpace(action.Customer!.Name):
                missing.Add("customer name");
                break;

            case TurnAction.EditCustomer when string.IsNullOrWhiteSpace(action.CustomerName) || action.Field is null || string.IsNullOrWhiteSpace(action.Value):
                missing.Add("customer and changed detail");
                break;

            case TurnAction.UpdateStatus when action.Status is null:
                missing.Add("status");
                break;

            case TurnAction.RecordExpense or TurnAction.MonthlyExpense when string.IsNullOrWhiteSpace(action.Value) || action.Amount is not > 0:
                missing.Add("expense and amount");
                break;
        }
        return missing;
    }

    /// <summary>Supplied values that break a rule (type, range, approved list). Labels name the field and the rule.</summary>
    public static IReadOnlyList<string> Invalid(TurnAction? action)
    {
        if (action is null) return Array.Empty<string>();
        var invalid = new List<string>();

        if (action.Action == TurnAction.CreateOrder)
        {
            foreach (var order in action.Orders)
                foreach (var item in order.Items)
                {
                    if (item.Quantity <= 0) invalid.Add("quantity (must be above 0)");
                    if (item.StatedUnitPrice is { } price && price < 0) invalid.Add("price (must not be negative)");
                }
        }

        foreach (var product in action.Products)
        {
            if (product.Price is { } p && p < 0) invalid.Add("price (must not be negative)");
            if (product.Stock is { } s && s < 0) invalid.Add("stock (must be 0 or more)");
        }

        if (action.Action is TurnAction.UpdateStatus && action.Status is { } status && !TurnAction.Statuses.Contains(status))
            invalid.Add("status (must be one of the approved statuses)");

        if (action.Action is TurnAction.EditCustomer && action.Field is { } field && !TurnAction.CustomerFields.Contains(field))
            invalid.Add("changed detail (must be phone, address, city or name)");

        // Expense amounts are checked in Missing (their legacy label); payments and payouts must carry a positive amount when one is said.
        if (action.Action is TurnAction.RecordPayment or TurnAction.RecordPayout && action.Amount is { } amount && amount <= 0)
            invalid.Add("amount (must be above 0)");

        return invalid.Distinct().ToList();
    }
}
