using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Screens 1c / 1c-2 / 1c-3: the interactive guide — "guide" offers a topic, then Next/Skip walks the steps.
public partial class ConversationEngine
{
    private static readonly string[] GuideOfferButtons = { "Guide dekhein", "Baad mein" };
    private static readonly string[] GuideTopicButtons = { "Poora guide", "Sirf orders", "Sirf payments" };
    private static readonly string[] GuideStepButtons = { "Next ➜", "Guide band karein" };

    private const string GuideOrdersBody =
        "Customer ka message copy/forward karein, ya khud likhein:\n\"Ayesha, 2 suit, 03001234567\"\nBas itna — bot baaki khud samajh lega.";

    private static readonly string[] GuideFullSteps =
    {
        "📖 Step 1/4 — Order Save Karna\n" + GuideOrdersBody,
        "📖 Step 2/4 — Orders Dekhna\nType karein: \"orders today\" ya \"pending orders\"\nTurant list mil jayegi.",
        "📖 Step 3/4 — Status Update\nOrder bhej diya? Likhein: \"mark 1 shipped\"\nDelivery ho gayi? \"mark 1 delivered\"",
        "📖 Step 4/4 — Kabhi Bhi Madad\nKuch bhool jayein to sirf \"menu\" ya \"guide\" likh dein — yeh guide dobara khul jayega.\n🎉 Bas itna hi — ab try karein!"
    };

    private const string GuideOrdersOnly = "📖 Order Save Karna\n" + GuideOrdersBody;

    private const string GuidePaymentsOnly =
        "📖 Payments\nCustomer ko payment details bhejni hain? Likhein: \"payment link\"\nPayment aa gayi? \"mark 4 paid\"\nApna JazzCash/Easypaisa number set karein: \"add payment: jazzcash, 0300-1234567\"";

    private async Task StartGuideAsync(Seller seller, ConversationSession session, SessionContextData ctx, string? topic, CancellationToken ct)
    {
        // Opened mid-onboarding (e.g. tapped from the catalog-step buttons): remember where to
        // resume, since an incomplete seller landing on Idle would otherwise restart onboarding.
        if (!seller.OnboardingComplete && ctx.GuideReturnState is null) ctx.GuideReturnState = session.State;
        SetState(session, ConversationState.AwaitingGuideStep);
        if (topic == "full")
        {
            ctx.GuideTopic = "full";
            ctx.GuideStep = 1;
            await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, GuideFullSteps[0], GuideStepButtons, ct);
            return;
        }

        ctx.GuideTopic = null;
        ctx.GuideStep = 0;
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, "📖 Guide kahan se shuru karein?", GuideTopicButtons, ct);
    }

    private async Task HandleGuideStepAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var choice = ButtonWords(message);

        if (ctx.GuideStep == 0)
        {
            switch (choice)
            {
                case "poora guide" or "poora guide 4 steps" or "full" or "1":
                    await StartGuideAsync(seller, session, ctx, "full", ct);
                    return;
                case "sirf orders" or "orders" or "2":
                    ExitGuide(session, ctx);
                    await ReplyAsync(seller, GuideOrdersOnly, ct);
                    return;
                case "sirf payments" or "payments" or "3":
                    ExitGuide(session, ctx);
                    await ReplyAsync(seller, GuidePaymentsOnly, ct);
                    return;
            }
        }
        else if (choice is "next" or "agla" or "aage")
        {
            ctx.GuideStep++;
            if (ctx.GuideStep >= GuideFullSteps.Length)
            {
                ExitGuide(session, ctx);
                await ReplyAsync(seller, GuideFullSteps[^1], ct);
            }
            else
            {
                await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, GuideFullSteps[ctx.GuideStep - 1], GuideStepButtons, ct);
            }
            return;
        }

        ExitGuide(session, ctx);
        if (choice is "guide band karein" or "band" or "skip" or "stop")
        {
            await ReplyAsync(seller, "Theek hai 👍 Jab bhi zaroorat ho, \"guide\" likh dein.", ct);
            return;
        }

        // Anything else is a normal message — leave the guide and handle it.
        await HandleIdleAsync(seller, session, ctx, message, ct);
    }

    private static void ExitGuide(ConversationSession session, SessionContextData ctx)
    {
        SetState(session, ctx.GuideReturnState ?? ConversationState.Idle);
        ctx.GuideReturnState = null;
        ctx.GuideTopic = null;
        ctx.GuideStep = 0;
    }
}
