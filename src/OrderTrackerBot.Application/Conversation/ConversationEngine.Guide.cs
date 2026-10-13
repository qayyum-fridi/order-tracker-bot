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
        "Customer ka message copy ya forward karein, ya khud likhein:\n\"Ayesha, 2 suit, 03001234567\"\nBaqi tafseel bot khud samajh kar order tayyar kar dega.";

    private static readonly string[] GuideFullSteps =
    {
        "📖 Step 1/4 — Order Save Karna\n" + GuideOrdersBody,
        "📖 Step 2/4 — Orders Dekhna\nLikhein: \"orders today\" ya \"pending orders\".\nOrders ki list foran bhej di jayegi.",
        "📖 Step 3/4 — Status Tabdeel Karna\nOrder rawana kar diya ho to likhein: \"mark 1 shipped\".\nDelivery mukammal ho jaye to likhein: \"mark 1 delivered\".",
        "📖 Step 4/4 — Madad\nKisi bhi waqt \"menu\" ya \"guide\" likhein, rehnumai dobara dastiyab hogi.\n🎉 Aap tayyar hain — ab pehla order likh kar dekhein."
    };

    private const string GuideOrdersOnly = "📖 Order Save Karna\n" + GuideOrdersBody;

    private const string GuidePaymentsOnly =
        "📖 Payments\nCustomer ko payment ki tafseel bhejne ke liye likhein: \"payment link\".\nPayment mosool hone par likhein: \"mark 4 paid\".\nApna JazzCash / Easypaisa number darj karne ke liye likhein: \"add payment: jazzcash, 0300-1234567\".";

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
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, "📖 Aap guide kahan se shuru karna chahenge?", GuideTopicButtons, ct);
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
            await ReplyAsync(seller, "ℹ️ Theek hai. Jab bhi zaroorat ho, \"guide\" likhein.", ct);
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
