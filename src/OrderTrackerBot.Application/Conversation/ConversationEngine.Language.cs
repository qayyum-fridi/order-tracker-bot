using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Screens 0 / 0b / 10e: post-language start choice, "change language" anytime, and the Business Setup summary.
// Wording lives in Templates/Messages/language.json; button labels stay here because the engine matches on them.
public partial class ConversationEngine
{
    // "Language badlein" was the third button; the seller has just picked a language, so that slot now offers moving data over (typing "change language" still works).
    private static readonly string[] StartChoiceButtons = { "Setup shuru karein", "Pehle guide dekhein", "📥 Purana data" };
    private static readonly string[] BusinessNameButtons = { "📖 Guide dekhein", "🌐 Language badlein", "Baad mein karunga" };
    private static readonly string[] AfterLanguageButtons = { "📋 Menu", "📦 New Order", "📖 Guide" };
    private static readonly string[] BusinessSetupButtons = { "Business Info", "Payment Method", "Language" };

    private static string LanguageChosenText(string language, bool onboarding) => (language, onboarding) switch
    {
        (Lang.UrduScript, true) => MessageTemplates.Get("language.chosen.onboarding.urdu"),
        (Lang.English, true) => MessageTemplates.Get("language.chosen.onboarding.english"),
        (_, true) => MessageTemplates.Get("language.chosen.onboarding.roman"),
        (Lang.UrduScript, false) => MessageTemplates.Get("language.chosen.urdu"),
        (Lang.English, false) => MessageTemplates.Get("language.chosen.english"),
        _ => MessageTemplates.Get("language.chosen.roman")
    };

    private Task AskBusinessNameAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.OnboardingBusinessName);
        return _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            MessageTemplates.Get("business.name.ask"), BusinessNameButtons, ct);
    }

    private Task AskLanguageAsync(Seller seller, CancellationToken ct) =>
        _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("language.ask"), Lang.ButtonLabels, ct);

    /// <summary>Called when the seller picks a language during onboarding (screen 0).</summary>
    private async Task OnboardingLanguageChosenAsync(Seller seller, ConversationSession session, string message, CancellationToken ct)
    {
        seller.PreferredLanguage = Lang.Resolve(message);
        SetState(session, ConversationState.OnboardingStartChoice);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, LanguageChosenText(seller.PreferredLanguage, true), StartChoiceButtons, ct);
    }

    private async Task HandleOnboardingStartChoiceAsync(Seller seller, ConversationSession session, string message, CancellationToken ct)
    {
        var choice = ButtonWords(message);
        if (choice.Contains("language"))
        {
            SetState(session, ConversationState.OnboardingLanguage);
            await AskLanguageAsync(seller, ct);
            return;
        }

        // "Purana data" (or typing import / import template customers): explain, offer the template files, and stay on this choice.
        // The seller then sends the filled file, or taps "Setup shuru karein" to carry on.
        if (choice is "purana data" or "puraana data" || CommandParser.TryParse(message) is { Kind: CommandKind.ImportHelp or CommandKind.ImportTemplate })
        {
            var import = CommandParser.TryParse(message);
            if (import is { Kind: CommandKind.ImportTemplate })
                await HandleImportTemplateAsync(seller, import.Text, ct);
            else
                await HandleImportHelpAsync(seller, ct);
            return;
        }

        if (choice.Contains("guide"))
            await ReplyAsync(seller, string.Join("\n\n", GuideStepsFor(seller)), ct);

        await AskBusinessNameAsync(seller, session, ct);
    }

    /// <summary>The three buttons under "Business ka naam bataiye?" must not be saved as the business name.</summary>
    private async Task<bool> TryOnboardingShortcutAsync(Seller seller, ConversationSession session, string message, CancellationToken ct)
    {
        var choice = ButtonWords(message);
        // "Pehle guide dekhein" is the start-step button label; sellers type it back when they reply to the question.
        if (choice is "guide dekhein" or "guide" or "pehle guide dekhein")
        {
            await ReplyAsync(seller, string.Join("\n\n", GuideStepsFor(seller)), ct);
            await AskBusinessNameAsync(seller, session, ct);
            return true;
        }

        if (choice is "language badlein" or "change language")
        {
            SetState(session, ConversationState.OnboardingLanguage);
            await AskLanguageAsync(seller, ct);
            return true;
        }

        if (choice is "baad mein karunga" or "baad mein")
        {
            await ReplyAsync(seller, MessageTemplates.Get("business.name.later"), ct);
            return true;
        }

        return false;
    }

    // Screen 0b: "change language" works anytime.
    private async Task StartChangeLanguageAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.AwaitingLanguageChoice);
        await AskLanguageAsync(seller, ct);
    }

    private async Task HandleLanguageChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var choice = ButtonWords(message);
        var isLanguage = choice is "roman urdu" or "english" or "urdu" or "urdu script" || message.Contains("اردو");
        if (!isLanguage)
        {
            SetState(session, ConversationState.Idle);
            await HandleIdleAsync(seller, session, ctx, message, ct);
            return;
        }

        SetState(session, ConversationState.Idle);
        seller.PreferredLanguage = Lang.Resolve(message);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, LanguageChosenText(seller.PreferredLanguage, false), AfterLanguageButtons, ct);
    }

    // Screen 10e: one place to see and update everything set during onboarding.
    private async Task HandleBusinessSetupAsync(Seller seller, CancellationToken ct)
    {
        var methods = await _db.PaymentMethods.Where(p => p.SellerId == seller.Id).OrderBy(p => p.Id).ToListAsync(ct);
        var payment = methods.Count == 0
            ? "—"
            : string.Join(", ", methods.Select(m => $"{PaymentMethodName(m.Type)} {m.AccountNumberOrId}"));
        var languageName = Lang.Normalize(seller.PreferredLanguage) switch
        {
            Lang.English => MessageTemplates.Get("language.name.english"),
            Lang.UrduScript => MessageTemplates.Get("language.name.urdu"),
            _ => MessageTemplates.Get("language.name.roman")
        };

        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            MessageTemplates.Get("business.setup.summary",
                seller.BusinessName, seller.City ?? "—", seller.BusinessType ?? "—", seller.InstagramHandle ?? "—", payment, languageName),
            BusinessSetupButtons, ct);
    }

    private async Task StartPaymentMethodInputAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.AwaitingPaymentMethodInput);
        await ReplyAsync(seller, MessageTemplates.Get("payment.method.prompt"), ct);
    }

    private async Task HandlePaymentMethodInputAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        if (CommandParser.TryParse(message) is { } other && other.Kind != CommandKind.AddPaymentMethod)
        {
            await ExecuteCommandAsync(seller, session, ctx, other, ct);
            return;
        }

        var command = CommandParser.TryParse($"add payment: {message.Replace("،", ",").Trim()}");
        if (command is { Kind: CommandKind.AddPaymentMethod, Text2: not null })
        {
            await HandleAddPaymentMethodAsync(seller, command, ct);
            return;
        }

        SetState(session, ConversationState.AwaitingPaymentMethodInput);
        await ReplyAsync(seller, MessageTemplates.Get("payment.method.format"), ct);
    }
}
