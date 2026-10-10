namespace OrderTrackerBot.Api.Admin;

public sealed record AdminStatsDto(
    int TotalSellers,
    int OnboardedSellers,
    int TotalOrders,
    int PendingOrders,
    int OrdersLast24Hours,
    decimal Revenue,
    IReadOnlyDictionary<string, int> SellersByPlan);

public sealed record SellerListItemDto(
    int Id,
    string WhatsAppPhoneNumber,
    string? BusinessName,
    string? City,
    string? BusinessType,
    bool OnboardingComplete,
    string Plan,
    DateTime? TrialEndsAt,
    DateTime? SubscriptionActiveUntil,
    DateTime CreatedAt,
    int OrderCount,
    string Status);

public sealed record SellerDetailDto(
    int Id,
    string WhatsAppPhoneNumber,
    string? BusinessName,
    string? City,
    string? BusinessType,
    bool OnboardingComplete,
    string PreferredLanguage,
    string TimeZoneId,
    string? InstagramHandle,
    string? Ntn,
    string? Strn,
    decimal DefaultDeliveryCharge,
    decimal SalesTaxRate,
    string Plan,
    DateTime? TrialEndsAt,
    DateTime? SubscriptionActiveUntil,
    DateTime CreatedAt,
    int OrderCount,
    int ProductCount,
    int CustomerCount,
    IReadOnlyList<OrderListItemDto> RecentOrders,
    string Status);

public sealed record OrderListItemDto(
    int Id,
    int SellerId,
    string? SellerBusinessName,
    string SellerPhone,
    string CustomerName,
    string Status,
    string PaymentStatus,
    decimal Total,
    decimal AmountPaid,
    int? ReceiptNumber,
    DateTime CreatedAt);

public sealed record PagedResultDto<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record UpdateSubscriptionRequest(string? Plan, DateTime? SubscriptionActiveUntil);

public sealed record UpdateSellerStatusRequest(string? Status);

/// <summary>Resetting a seller needs their phone number typed again, so a misclick cannot wipe an account.</summary>
public sealed record ResetAccountRequest(string? ConfirmPhone);
