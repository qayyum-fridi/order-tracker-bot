using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Screens 0 / 0b / 10e: post-language start choice, "change language" anytime, and the Business Setup summary.
public partial class ConversationEngine
{
    private static readonly string[] StartChoiceButtons = { "Setup shuru karein", "Pehle guide dekhein", "🌐 Language badlein" };
    private static readonly string[] BusinessNameButtons = { "📖 Guide dekhein", "🌐 Language badlein", "Baad mein karunga" };
    private static readonly string[] AfterLanguageButtons = { "📋 Menu", "📦 New Order", "📖 Guide" };
    private static readonly string[] BusinessSetupButtons = { "Business Info", "Payment Method", "Language" };

    private static string LanguageChosenText(string language, bool onboarding) => (language, onboarding) switch
    {
        (Lang.UrduScript, true) => "✅ ٹھیک ہے، ہم اردو میں بات کریں گے۔\n(ٹائپ کر کے جواب دینا بھی چلتا ہے — زبان کبھی بھی \"change language\" سے بدل سکتے ہیں)\n\nاب کیا کرنا چاہیں گے؟",
        (Lang.English, true) => "✅ Alright, we'll continue in English.\n(You can also just type your reply — change the language anytime with \"change language\")\n\nWhat would you like to do?",
        (_, true) => "✅ Theek hai, Roman Urdu mein baat karenge.\n(Typed reply bhi chal jata hai, button zaroori nahi — language kabhi bhi \"change language\" se badal saktay hain)\n\nAb kya karna chahenge?",
        (Lang.UrduScript, false) => "✅ ٹھیک ہے — اب اردو میں بات ہوگی۔\n\nکیا کرنا چاہیں گے؟",
        (Lang.English, false) => "✅ Great — switching to English. All replies (menu, guide, orders) will now show in English.\n\nWhat would you like to do?",
        _ => "✅ Theek hai — ab Roman Urdu mein baat hogi.\n\nKya karna chahenge?"
    };

    private Task AskBusinessNameAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.OnboardingBusinessName);
        return _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            "Business ka naam bataiye? (ya neeche se option chunein)", BusinessNameButtons, ct);
    }

    private Task AskLanguageAsync(Seller seller, CancellationToken ct) =>
        _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, "🌐 Konsi language mein baat karein?", Lang.ButtonLabels, ct);

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

        if (choice.Contains("guide"))
            await ReplyAsync(seller, string.Join("\n\n", GuideFullSteps), ct);

        await AskBusinessNameAsync(seller, session, ct);
    }

    /// <summary>The three buttons under "Business ka naam bataiye?" must not be saved as the business name.</summary>
    private async Task<bool> TryOnboardingShortcutAsync(Seller seller, ConversationSession session, string message, CancellationToken ct)
    {
        var choice = ButtonWords(message);
        if (choice is "guide dekhein" or "guide")
        {
            await ReplyAsync(seller, string.Join("\n\n", GuideFullSteps), ct);
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
            await ReplyAsync(seller, "Theek hai 👍 Jab tayyar hon, bas apne business ka naam bhej dein.", ct);
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
            Lang.English => "English",
            Lang.UrduScript => "اردو",
            _ => "Roman Urdu"
        };

        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            $"⚙️ Business Setup — {seller.BusinessName}\n" +
            $"📍 City: {seller.City ?? "—"}\n" +
            $"🏷️ Type: {seller.BusinessType ?? "—"}\n" +
            $"📸 Instagram: {seller.InstagramHandle ?? "—"}\n" +
            $"💳 Payment: {payment}\n" +
            $"🌐 Language: {languageName}\n\n" +
            "Kya update karna hai?", BusinessSetupButtons, ct);
    }

    private async Task StartPaymentMethodInputAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.AwaitingPaymentMethodInput);
        await ReplyAsync(seller, "💳 Naya payment method bhejein (e.g. \"easypaisa, 0300-1234567\")", ct);
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
        await ReplyAsync(seller, "Format yeh hai: \"easypaisa, 0300-1234567\" (jazzcash / easypaisa / bank).", ct);
    }
}
