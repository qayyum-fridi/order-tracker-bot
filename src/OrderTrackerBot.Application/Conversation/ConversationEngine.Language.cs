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
    private string[] StartChoiceButtonsFor() => _features.OldDataImport ? StartChoiceButtons : StartChoiceButtons[..2];
    private static readonly string[] BusinessNameButtons = { "📖 Guide dekhein", "🌐 Language badlein", "Baad mein karunga" };
    // The guide was just sent above the question, so its button is left out.
    private static readonly string[] BusinessNameButtonsAfterGuide = { "🌐 Language badlein", "Baad mein karunga" };
    private static readonly string[] AfterLanguageButtons = { "📋 Menu", "📦 New Order", "📖 Guide" };
    private static readonly string[] BusinessSetupButtons = { "Business Info", "Payment Method", "Language" };
    // Normalised (ButtonWords) labels of the start and name-step buttons, so a tap is never saved as a shop name.
    private static readonly HashSet<string> ButtonLikeOnboardingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup shuru karein", "pehle guide dekhein", "guide dekhein", "purana data", "puraana data",
        "language badlein", "change language", "baad mein karunga", "baad mein"
    };

    private static string LanguageChosenText(string language, bool onboarding) => (language, onboarding) switch
    {
        (Lang.UrduScript, true) => MessageTemplates.Get("language.chosen.onboarding.urdu"),
        (Lang.English, true) => MessageTemplates.Get("language.chosen.onboarding.english"),
        (_, true) => MessageTemplates.Get("language.chosen.onboarding.roman"),
        (Lang.UrduScript, false) => MessageTemplates.Get("language.chosen.urdu"),
        (Lang.English, false) => MessageTemplates.Get("language.chosen.english"),
        _ => MessageTemplates.Get("language.chosen.roman")
    };

    private Task AskBusinessNameAsync(Seller seller, ConversationSession session, CancellationToken ct, bool guideShown = false)
    {
        SetState(session, ConversationState.OnboardingBusinessName);
        return _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            MessageTemplates.Get("business.name.ask"), guideShown ? BusinessNameButtonsAfterGuide : BusinessNameButtons, ct);
    }

    private Task AskLanguageAsync(Seller seller, CancellationToken ct) =>
        _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("language.ask"), Lang.ButtonLabels, ct);

    /// <summary>Called when the seller picks a language during onboarding (screen 0).</summary>
    private async Task OnboardingLanguageChosenAsync(Seller seller, ConversationSession session, string message, CancellationToken ct)
    {
        seller.PreferredLanguage = Lang.Resolve(message);
        SetState(session, ConversationState.OnboardingStartChoice);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, LanguageChosenText(seller.PreferredLanguage, true), StartChoiceButtonsFor(), ct);
    }

    /// <summary>A language button tapped again after the language is already chosen (duplicate tap). Matched on the same normalised words as the other buttons.</summary>
    private static bool IsLanguageButton(string message) =>
        Lang.ButtonLabels.Any(label => ButtonWords(label) == ButtonWords(message));

    private async Task HandleOnboardingStartChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        // "1" / "2" / "3" typed instead of a tap: the button in that place.
        var buttons = StartChoiceButtonsFor();
        if (int.TryParse(message.Trim(), out var picked) && picked >= 1 && picked <= buttons.Length)
            message = buttons[picked - 1];
        // The shop's name typed straight away ("Ali Clothing") answers the next question: it is kept, not dropped and asked for again.
        else if (IsTypedShopName(message))
        {
            SetState(session, ConversationState.OnboardingBusinessName);
            await HandleOnboardingAsync(seller, session, ctx, message, ct);
            return;
        }

        // A repeated language tap must not restart the language step or lose the start choice: show the start choice again.
        if (IsLanguageButton(message))
        {
            await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, LanguageChosenText(seller.PreferredLanguage, true), StartChoiceButtons, ct);
            return;
        }

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

        await AskBusinessNameAsync(seller, session, ct, guideShown: choice.Contains("guide"));
    }

    /// <summary>The three buttons under "Business ka naam bataiye?" must not be saved as the business name.</summary>
    private async Task<bool> TryOnboardingShortcutAsync(Seller seller, ConversationSession session, string message, CancellationToken ct)
    {
        var choice = ButtonWords(message);
        // "Pehle guide dekhein" is the start-step button label; sellers type it back when they reply to the question.
        if (choice is "guide dekhein" or "guide" or "pehle guide dekhein")
        {
            await ReplyAsync(seller, string.Join("\n\n", GuideStepsFor(seller)), ct);
            await AskBusinessNameAsync(seller, session, ct, guideShown: true);
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

        // A start-step button that reached the name question (a repeated or mangled tap) is never the shop name: ask again, move on only after a real name.
        if (ButtonLikeOnboardingWords.Contains(choice))
        {
            await AskBusinessNameAsync(seller, session, ct);
            return true;
        }

        // "1", "2", "..." has no letter: not a shop name. Say so and ask again instead of saving it.
        if (!message.Any(char.IsLetter))
        {
            await ReplyAsync(seller, MessageTemplates.Get("business.name.invalid", message.Trim()), ct);
            await AskBusinessNameAsync(seller, session, ct);
            return true;
        }

        return false;
    }

    // Words that answer the start question itself ("setup", "haan", "ok") rather than name the shop.
    private static readonly HashSet<string> StartChoiceAnswerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "setup karein", "setup karo", "shuru", "shuru karein", "start", "guide", "haan", "han", "ha", "yes", "ji haan", "no", "nahi"
    };

    /// <summary>Typed text at the start choice that is the shop's name: has a letter, is no button, answer word, acknowledgement or command.</summary>
    private static bool IsTypedShopName(string message)
    {
        var words = ButtonWords(message);
        return message.Any(char.IsLetter) && words.Length > 0
               && !ButtonLikeOnboardingWords.Contains(words) && !StartChoiceAnswerWords.Contains(words) && !AcknowledgementWords.Contains(words)
               && !words.Contains("setup") && !words.Contains("guide") && !words.Contains("language") && !words.Contains("purana") && !words.Contains("import")
               && !IsLanguageButton(message) && !CommandParser.IsAffirmative(message) && !CommandParser.IsNegative(message)
               && CommandParser.TryParse(message) is null;
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
            BusinessSetupButtons.Where(b => !IsCutCommandText(b)).ToArray(), ct);
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
        if (IsOutOfMvp(CommandKind.AddPaymentMethod))
        {
            // A state left from before the MVP cut: the method is not saved.
            await ReplyAsync(seller, MessageTemplates.Get("mvp.001"), ct);
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
