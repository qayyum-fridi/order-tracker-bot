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
