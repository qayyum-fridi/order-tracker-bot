namespace OrderTrackerBot.Api.Client;

// Field names match the dashboard's contract (src/lib/api/types.ts). Enums are sent as their names.

public sealed record IssuedClientTokenDto(string Token);

public sealed record ClientProfileDto(
    int Id, string Phone, string? BusinessName, string? City, string? BusinessType, string? Email, string TimeZoneId,
    bool OnboardingComplete, string Plan, string Status, DateTime? TrialEndsAt, DateTime? SubscriptionActiveUntil, DateTime CreatedAt);

public sealed record PagedDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record DateRangeDto(string From, string To);

public sealed record CustomerDto(
    int Id, string Name, string? Phone, string? City, string? Address, string? PreferredContact, string? Notes,
    int OrderCount, decimal TotalSpent, DateTime? LastOrderAt, DateTime CreatedAt);

public sealed record ProductDto(
    int Id, string Name, decimal Price, decimal? CostPrice, string UnitType, decimal UnitQty, string? Category,
    string? Subcategory, string? Size, string? Color, string? Sku, int? StockQty, bool IsActive, DateTime CreatedAt);

public sealed record OrderSummaryDto(
    int Id, int CustomerId, string CustomerName, string Status, string PaymentStatus, string PaymentMethod,
    decimal Total, decimal AmountPaid, int? ReceiptNumber, DateTime CreatedAt);

public sealed record OrderLineDto(int Id, int? ProductId, string ProductName, decimal UnitPrice, decimal? UnitCost, int Quantity, decimal LineTotal);

public sealed record OrderDetailDto(
    int Id, int CustomerId, string CustomerName, string Status, string PaymentStatus, string PaymentMethod,
    decimal Total, decimal AmountPaid, int? ReceiptNumber, DateTime CreatedAt,
    decimal Subtotal, decimal DiscountAmount, string? DiscountCode, decimal DeliveryCharge,
    string? TrackingCourier, string? TrackingNumber, string? Notes,
    DateTime? ShippedAt, DateTime? DeliveredAt, DateTime? CancelledAt, DateTime? ReturnedAt,
    IReadOnlyList<OrderLineDto> Items);

public sealed record KpisDto(
    decimal Revenue, int Orders, int PendingOrders, int ActiveCustomers, decimal AverageOrderValue, double? RevenueDeltaPct);

public sealed record DayPointDto(string Date, decimal Revenue, int Orders);

public sealed record CategoryShareDto(string Category, decimal Revenue, double Share, int Units);

public sealed record DashboardDto(
    DateRangeDto Range, KpisDto Kpis, IReadOnlyList<DayPointDto> RevenueByDay,
    IReadOnlyDictionary<string, int> StatusCounts, IReadOnlyList<CategoryShareDto> CategoryMix,
    IReadOnlyList<OrderSummaryDto> RecentOrders, IReadOnlyList<ProductDto> LowStock,
    TodayDto Today, OwedDto Owed);

/// <summary>Today's sales in Pakistan time, whatever range the dashboard shows. Cancelled and returned orders are not counted.</summary>
public sealed record TodayDto(string Date, decimal Sales, int Orders);

/// <summary>Money still to collect across all open sales (total minus amount paid), not only the range shown.</summary>
public sealed record OwedDto(decimal Total, int Orders);

/// <summary>Days since the last order or expense. Status: ok (0-1 days), behind (2-3), stale (4+ or never).</summary>
public sealed record EntryActivityDto(
    string? LastEntryDay, int? DaysSinceLastEntry, int StreakDays, IReadOnlyList<string> MissedDaysLast14, string Status);

public sealed record CategorySubDto(string Name, int ProductCount, decimal Revenue);

public sealed record CategoryDto(string Name, int ProductCount, decimal Revenue, IReadOnlyList<CategorySubDto> Subcategories);

public sealed record SetupStepDto(string Key, string Label, bool Done, string Href);

public sealed record SetupDto(IReadOnlyList<SetupStepDto> Steps, int StepsDone, int StepsTotal, string? StuckReason);
