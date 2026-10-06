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
    AwaitingResetConfirmation,
    OnboardingLanguage,
    AwaitingDiscountDetails,
    // Appended only — states are stored as ints.
    OnboardingOptionalDetails,
    AwaitingBusinessInfo,
    AwaitingSubscriptionPayment,
    AwaitingReceiptOrderChoice,
    AwaitingDeleteCustomerConfirmation,
    AwaitingLoyaltyDiscountConfirmation,
    AwaitingMultiOrderConfirmation,
    AwaitingBroadcastChannelChoice,
    AwaitingSupportReplyConfirmation,
    AwaitingSupportReplyEdit,
    AwaitingSupportQueryPick,
    AwaitingGuideStep,
    OnboardingStartChoice,
    AwaitingLanguageChoice,
    AwaitingPaymentMethodInput,
    AwaitingOrderEdit
}

public enum OrderStatus
{
    Pending,
    Shipped,
    Delivered,
    Cancelled,
    /// <summary>Came back (refused/returned COD). Like Cancelled, never counted as a sale.</summary>
    Returned
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
    ProductPriceChanged,
    OrderEdited,
    CustomerUpdated
}

public enum SubscriptionPlan
{
    Trial,
    Basic,
    Pro
}
