namespace OrderTrackerBot.Domain.Enums;

public enum ConversationState
{
    Idle,
    OnboardingBusinessName,
    OnboardingCatalogSize,
    OnboardingAddProduct,
    AwaitingOrderConfirmation,
    AwaitingOrderMissingFields,
    AwaitingClarificationChoice,
    AwaitingCancelConfirmation,
    AwaitingBulkStatusConfirmation,
    AwaitingDuplicateOrderConfirmation,
    AwaitingCodCollectedConfirmation,
    AwaitingRuntimeFilterChoice,
    AwaitingCustomDateRange,
    AwaitingOrderGroupingChoice,
    AwaitingBroadcastAudienceChoice,
    AwaitingResetConfirmation
}

public enum OrderStatus
{
    Pending,
    Shipped,
    Delivered,
    Cancelled
}

public enum PaymentStatus
{
    Unpaid,
    Paid
}

public enum OrderPaymentMethod
{
    Unspecified,
    Cod,
    Manual,
    Gateway
}

public enum SellerPaymentMethodType
{
    JazzCash,
    Easypaisa,
    Bank,
    Safepay
}

public enum DiscountType
{
    Percent,
    Flat
}

public enum ActionType
{
    OrderCreated,
    OrderStatusChanged,
    OrderCancelled,
    ProductPriceChanged
}
