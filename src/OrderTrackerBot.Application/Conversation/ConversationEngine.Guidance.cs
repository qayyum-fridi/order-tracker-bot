using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

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
            $"ℹ️ {GuidanceFor(session.State, ctx)}\n\n" +
            "🎤 Text ya voice note — dono chalte hain.\n" +
            "• Poori command list: \"help\"\n• Step-by-step guide: \"guide\"\n• Kisi bhi waqt dobara poochne ke liye: \"kya karun\"", ct);
    }

    private static string GuidanceFor(ConversationState state, SessionContextData ctx) => state switch
    {
        ConversationState.Idle => "Setup shuru karne ke liye \"start\" likhein (ya bol dein).",
        ConversationState.OnboardingLanguage or ConversationState.AwaitingLanguageChoice => "Apni zubaan chunein: Roman Urdu, English ya اردو (neeche buttons hain).",
        ConversationState.OnboardingStartChoice => "Chunein: \"Setup shuru karein\", \"Guide dekhein\" ya \"Baad mein karunga\".",
        ConversationState.OnboardingBusinessName => "Apni dukaan ka naam batayein, jaise \"Ayesha Collections\".",
        ConversationState.OnboardingOptionalDetails => "Shehar, karobar ki qisam aur Instagram handle ek saath bhejein, jaise \"Lahore, Clothing, @ayesha.collections\" — ya \"skip\" likhein.",
        ConversationState.OnboardingCatalogSize => "Batayein aapke paas kitne products hain: \"10 ke qareeb\", ya button dabayein.",
        ConversationState.OnboardingAddProduct =>
            "Har product ka naam aur price bhejein, jaise \"Lawn Suit - 3500\" (kai products ek saath bhi, har line mein ek). " +
            "Sirf naam bataya to main price pooch loon ga. Khatam hone par \"done\" likhein.",
        ConversationState.AwaitingOrderConfirmation =>
            "Order draft confirm karne ke liye YES likhein. Badalna ho to: \"delivery 300\", \"advance 500\". Rokne ke liye \"cancel\".",
        ConversationState.AwaitingOrderMissingFields =>
            $"Order ki baqi detail bhejein ({ctx.PendingMissingField ?? "customer ka naam ya phone"}). Rokne ke liye \"cancel\".",
        ConversationState.AwaitingMultiOrderConfirmation => "Sab orders confirm karne ke liye YES, ya sirf ek ka number bhejein.",
        ConversationState.AwaitingClarificationChoice or ConversationState.AwaitingOrderGroupingChoice or ConversationState.AwaitingRuntimeFilterChoice
            or ConversationState.AwaitingBroadcastAudienceChoice or ConversationState.AwaitingBroadcastChannelChoice or ConversationState.AwaitingReceiptOrderChoice
            or ConversationState.AwaitingSupportQueryPick => "Upar diye options mein se number bhejein (1 ya 2) — bolte waqt \"pehla\" / \"dusra\" bhi chalta hai.",
        ConversationState.AwaitingCancelConfirmation or ConversationState.AwaitingBulkStatusConfirmation or ConversationState.AwaitingDuplicateOrderConfirmation
            or ConversationState.AwaitingCodCollectedConfirmation or ConversationState.AwaitingResetConfirmation or ConversationState.AwaitingDeleteCustomerConfirmation
            or ConversationState.AwaitingLoyaltyDiscountConfirmation or ConversationState.AwaitingSupportReplyConfirmation => "YES ya NO likhein (bol kar \"haan\" / \"nahi\" bhi chalta hai).",
        ConversationState.AwaitingVoiceConfirmation => "Aapki voice se jo actions samjhe, woh upar likhe hain — chalane ke liye YES, rokne ke liye NO.",
        ConversationState.AwaitingOrderEdit => OrderEditHelp,
        ConversationState.AwaitingDiscountDetails => "Discount code likhein, jaise \"create discount: EID10, 10 percent, expires 15 days\".",
        ConversationState.AwaitingPaymentMethodInput => "Payment method likhein, jaise \"add payment: jazzcash, 0300-1234567\".",
        _ => "Bot abhi aapke jawab ka intezar kar raha hai. Upar wale sawal ka jawab dein, ya \"cancel\" likh kar ruk jayein."
    };
}
