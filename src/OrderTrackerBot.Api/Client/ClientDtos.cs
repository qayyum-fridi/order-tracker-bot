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

/// <summary>One line of the seller's WhatsApp chat with the bot. Direction is "inbound" (seller → bot) or "outbound" (bot → seller).</summary>
public sealed record ChatMessageDto(int Id, DateTime At, string Direction, string Text);

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
    IReadOnlyList<OrderSummaryDto> RecentOrders, IReadOnlyList<ProductDto> LowStock);

public sealed record CategorySubDto(string Name, int ProductCount, decimal Revenue);

public sealed record CategoryDto(string Name, int ProductCount, decimal Revenue, IReadOnlyList<CategorySubDto> Subcategories);

public sealed record SetupStepDto(string Key, string Label, bool Done, string Href);

public sealed record SetupDto(IReadOnlyList<SetupStepDto> Steps, int StepsDone, int StepsTotal, string? StuckReason);
