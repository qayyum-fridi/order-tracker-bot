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
    AwaitingOrderEdit,
    /// <summary>Voice note read as risky actions (price edit, cancel, status change...); waiting for YES/NO before running them.</summary>
    AwaitingVoiceConfirmation,
    /// <summary>A customers/catalog file was uploaded and checked; waiting for YES before anything is saved.</summary>
    AwaitingImportConfirmation
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
    CustomerUpdated,
    ExpenseAdded,
    DataImported,
    LossLogged,
    WageLogged
}

/// <summary>Admin-set account state. Disabled and Cancelled sellers get a fixed reply and nothing else is processed.
/// "Expired" is not stored: it follows from TrialEndsAt / SubscriptionActiveUntil.</summary>
public enum SellerStatus
{
    Active,
    Disabled,
    Cancelled
}

public enum SubscriptionPlan
{
    Trial,
    Basic,
    Pro
}
