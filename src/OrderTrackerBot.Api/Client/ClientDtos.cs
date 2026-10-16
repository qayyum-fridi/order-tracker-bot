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
    int OrderCount, decimal TotalSpent, DateTime? LastOrderAt, DateTime CreatedAt, string? PriceGroup);

/// <summary>One line of the seller's WhatsApp chat with the bot. Direction is "inbound" (seller → bot) or "outbound" (bot → seller).</summary>
public sealed record ChatMessageDto(int Id, DateTime At, string Direction, string Text);

public sealed record ProductDto(
    int Id, string Name, decimal Price, decimal? CostPrice, string UnitType, decimal UnitQty, string? Category,
    string? Subcategory, string? Size, string? Color, string? Sku, int? StockQty, bool IsActive, DateTime CreatedAt,
    string? DefaultPriceGroup, string? ItemKind);

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

public sealed record TopProductDto(string ProductName, int Units, decimal Revenue, decimal Profit);

/// <summary>Sales per payment method (Cod, Manual, Gateway, Unspecified). Profit is left out: it needs the product cost.</summary>
public sealed record PaymentMixDto(string Method, int Orders, decimal Revenue);

public sealed record AnalysisDto(
    DateRangeDto Range, KpisDto Kpis, IReadOnlyList<DayPointDto> Daily, IReadOnlyList<TopProductDto> TopProducts,
    IReadOnlyList<CategoryShareDto> CategoryMix, IReadOnlyList<PaymentMixDto> PaymentMix,
    IReadOnlyDictionary<string, int> StatusCounts, double RepeatCustomerRate, int NewCustomers, decimal GrossProfit);

/// <summary>One column of a report: its key in each row, the heading, and how to show it (text, number, money, percent or date).</summary>
public sealed record ReportColumnDto(string Key, string Label, string Type);

/// <summary>A report as a table. Rows are keyed by column key; Totals holds sums for the summable columns (null when none had a number).</summary>
public sealed record ReportTableDto(
    string Id, string Title, DateRangeDto Range, IReadOnlyList<ReportColumnDto> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, IReadOnlyDictionary<string, decimal?> Totals, string? Note);

public sealed record CategorySubDto(string Name, int ProductCount, decimal Revenue);

public sealed record CategoryDto(string Name, int ProductCount, decimal Revenue, IReadOnlyList<CategorySubDto> Subcategories);

public sealed record SetupStepDto(string Key, string Label, bool Done, string Href);

public sealed record SetupDto(IReadOnlyList<SetupStepDto> Steps, int StepsDone, int StepsTotal, string? StuckReason);

public sealed record PaymentMethodDto(int Id, string Type, string AccountNumberOrId);

public sealed record SellerSettingsDto(
    string? BusinessName, string? City, string? BusinessType, string? Email, string? InstagramHandle,
    decimal DefaultDeliveryCharge, decimal SalesTaxRate, string? Ntn, string? Strn,
    string ReceiptSize, string? ReceiptHeader, string? ReceiptFooter, string? ReceiptContact,
    IReadOnlyList<PaymentMethodDto> PaymentMethods);

/// <summary>A discount code. ExpiresAt is the last Pakistan day it works (yyyy-MM-dd), or null for no expiry.</summary>
public sealed record DiscountDto(int Id, string Code, string Type, decimal Value, string? ExpiresAt, bool IsActive);

public sealed record ExpenseDto(int Id, decimal Amount, string Category, string? Note, DateTime CreatedAt);
public sealed record WageDto(int Id, string WorkerName, decimal Amount, int? Days, DateTime CreatedAt);
public sealed record LossDto(int Id, int? ProductId, string ProductName, int Quantity, decimal UnitCost, string? Reason, DateTime CreatedAt);

/// <summary>A list of money-out or write-off rows for a date range, with the total.</summary>
public sealed record MoneyListDto<T>(IReadOnlyList<T> Items, decimal Total);

public sealed record ProductStockDto(int Id, string Name, int? StockQty);
