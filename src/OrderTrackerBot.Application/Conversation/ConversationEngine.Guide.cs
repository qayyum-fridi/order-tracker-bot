using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Screens 1c / 1c-2 / 1c-3: the interactive guide — "guide" offers a topic, then Next/Skip walks the steps.
public partial class ConversationEngine
{
    private static readonly string[] GuideOfferButtons = { "Guide dekhein", "Baad mein" };
    private static readonly string[] GuideTopicButtons = { "Poora guide", "Sirf orders", "Sirf payments" };
    private static readonly string[] GuideStepButtons = { "Next ➜", "Guide band karein" };

    private static string GuideOrdersBodyFor(Seller? seller) =>
        MessageTemplates.Get("guide.004", IndustryProfiles.Resolve(seller?.BusinessType).OrderExample);

    /// <summary>The 4-step guide; step 1's order example is the one of the seller's kind of shop.</summary>
    private static string[] GuideStepsFor(Seller? seller)
    {
        return new[]
        {
            MessageTemplates.Get("guide.006", GuideOrdersBodyFor(seller)),
            MessageTemplates.Get("guide.007"),
            MessageTemplates.Get("guide.008"),
            MessageTemplates.Get("guide.009")
        };
    }


    private static readonly string GuidePaymentsOnly =
        MessageTemplates.Get("guide.005");

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
            await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, GuideStepsFor(seller)[0], GuideStepButtons, ct);
            return;
        }

        ctx.GuideTopic = null;
        ctx.GuideStep = 0;
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("guide.001"), GuideTopicButtons, ct);
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
                    await ReplyAsync(seller, MessageTemplates.Get("guide.002", GuideOrdersBodyFor(seller)), ct);
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
            var steps = GuideStepsFor(seller);
            if (ctx.GuideStep >= steps.Length)
            {
                ExitGuide(session, ctx);
                await ReplyAsync(seller, steps[^1], ct);
            }
            else
            {
                await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, steps[ctx.GuideStep - 1], GuideStepButtons, ct);
            }
            return;
        }

        ExitGuide(session, ctx);
        if (choice is "guide band karein" or "band" or "skip" or "stop")
        {
            await ReplyAsync(seller, MessageTemplates.Get("guide.003"), ct);
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
