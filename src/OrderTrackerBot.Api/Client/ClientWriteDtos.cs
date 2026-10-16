namespace OrderTrackerBot.Api.Client;

/// <summary>Seller settings the dashboard can change. Payment methods have their own routes.</summary>
public sealed record SellerSettingsInput(
    string? BusinessName, string? City, string? BusinessType, string? Email, string? InstagramHandle,
    decimal? DefaultDeliveryCharge, decimal? SalesTaxRate, string? Ntn, string? Strn,
    string? ReceiptSize, string? ReceiptHeader, string? ReceiptFooter, string? ReceiptContact);

public sealed record PaymentMethodInput(string? Type, string? AccountNumberOrId);

public sealed record CustomerInput(
    string? Name, string? Phone, string? City, string? Address, string? PreferredContact, string? Notes, string? PriceGroup);

public sealed record DiscountInput(string? Code, string? Type, decimal? Value, string? ExpiresAt, bool? IsActive);

public sealed record DiscountActiveInput(bool IsActive);

public sealed record OrderStatusInput(string? Status);

public sealed record CategoryRenameInput(string? From, string? To);

public sealed record ExpenseInput(decimal? Amount, string? Category, string? Note, string? Date);

public sealed record WageInput(string? WorkerName, decimal? Amount, int? Days, string? Date);

public sealed record LossInput(int? ProductId, string? ProductName, int? Quantity, string? Reason);

public sealed record StockInput(int? StockQty);

public sealed record NewOrderLineInput(int? ProductId, int? Quantity);

/// <summary>A new order from the dashboard. Prices come from the catalog (the customer's price group when set); the discount code is optional.</summary>
public sealed record NewOrderInput(
    int? CustomerId, IReadOnlyList<NewOrderLineInput>? Items, string? PaymentMethod, decimal? AmountPaid,
    decimal? DeliveryCharge, string? DiscountCode, string? Notes);
