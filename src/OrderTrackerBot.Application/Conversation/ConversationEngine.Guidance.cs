using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// "kya karun" / "kaise karun" / "?" (typed or spoken): a short tip for exactly where the seller is right now. It never changes the state,
// so a half-finished order or setup step survives the question.
public partial class ConversationEngine
{
    private async Task SendContextualGuideAsync(Seller seller, ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        // A finished seller with nothing pending just gets the normal command list.
        if (session.State == ConversationState.Idle && seller.OnboardingComplete)
        {
            await ExecuteCommandAsync(seller, session, ctx, new ParsedCommand { Kind = CommandKind.Help }, ct);
            return;
        }

        await ReplyAsync(seller,
            MessageTemplates.Get("guidance.001", GuidanceFor(session.State, ctx, seller)), ct);
    }

    private static string GuidanceFor(ConversationState state, SessionContextData ctx, Seller? seller = null) => state switch
    {
        ConversationState.Idle => MessageTemplates.Get("guidance.002"),
        ConversationState.OnboardingLanguage or ConversationState.AwaitingLanguageChoice => MessageTemplates.Get("guidance.003"),
        ConversationState.OnboardingStartChoice => MessageTemplates.Get("guidance.004"),
        ConversationState.OnboardingBusinessName => MessageTemplates.Get("guidance.005"),
        ConversationState.OnboardingIndustry => MessageTemplates.Get("guidance.006"),
        ConversationState.OnboardingOptionalDetails => MessageTemplates.Get("guidance.007"),
        ConversationState.OnboardingCatalogSize => MessageTemplates.Get("guidance.008"),
        ConversationState.OnboardingAddProduct =>
            "Har product ka naam aur price bhejein, jaise \"" + IndustryProfiles.Resolve(seller?.BusinessType).MainLine + "\" (kai products ek saath bhi, har line mein ek). " +
            "Agar sirf naam likha ho to main price daryaft kar loon ga. Mukammal hone par \"done\" likhein.",
        ConversationState.AwaitingOrderConfirmation =>
            MessageTemplates.Get("guidance.009"),
        ConversationState.AwaitingOrderMissingFields =>
            MessageTemplates.Get("guidance.010", ctx.PendingMissingField ?? "customer ka naam ya phone"),
        ConversationState.AwaitingMultiOrderConfirmation => MessageTemplates.Get("guidance.011"),
        ConversationState.AwaitingClarificationChoice or ConversationState.AwaitingOrderGroupingChoice or ConversationState.AwaitingRuntimeFilterChoice
            or ConversationState.AwaitingBroadcastAudienceChoice or ConversationState.AwaitingBroadcastChannelChoice or ConversationState.AwaitingReceiptOrderChoice
            or ConversationState.AwaitingSupportQueryPick => MessageTemplates.Get("guidance.012"),
        ConversationState.AwaitingCancelConfirmation or ConversationState.AwaitingBulkStatusConfirmation or ConversationState.AwaitingDuplicateOrderConfirmation
            or ConversationState.AwaitingCodCollectedConfirmation or ConversationState.AwaitingResetConfirmation or ConversationState.AwaitingDeleteCustomerConfirmation
            or ConversationState.AwaitingLoyaltyDiscountConfirmation or ConversationState.AwaitingSupportReplyConfirmation
            or ConversationState.AwaitingImportConfirmation => MessageTemplates.Get("guidance.013"),
        ConversationState.AwaitingVoiceConfirmation => MessageTemplates.Get("guidance.014"),
        ConversationState.AwaitingOrderEdit => OrderEditHelp,
        ConversationState.AwaitingReceiptDesign => ReceiptDesignHelp,
        ConversationState.AwaitingDiscountDetails => MessageTemplates.Get("guidance.015"),
        ConversationState.AwaitingPaymentMethodInput => MessageTemplates.Get("guidance.016"),
        _ => MessageTemplates.Get("guidance.017")
    };
}
