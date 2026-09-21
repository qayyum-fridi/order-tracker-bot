using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private static readonly Regex ProductLine = new(@"^(.+?)\s*-\s*(\d+(?:\.\d+)?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DoneOrSkip = new(@"^(done|skip)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task HandleOnboardingAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        switch (session.State)
        {
            case ConversationState.OnboardingBusinessName:
                seller.BusinessName = message;
                SetState(session, ConversationState.OnboardingCatalogSize);
                await ReplyAsync(seller, "Great! Ab products add karte hain. Kitne products hain — 20 se kam ya zyada?", ct);
                return;

            case ConversationState.OnboardingCatalogSize:
                SetState(session, ConversationState.OnboardingAddProduct);
                await ReplyAsync(seller, "Theek hai, ek ek karke bataiye — naam aur price (e.g. 'Lawn Suit - 3500')", ct);
                return;

            case ConversationState.OnboardingAddProduct:
                if (DoneOrSkip.IsMatch(message))
                {
                    seller.OnboardingComplete = true;
                    SetState(session, ConversationState.Idle);
                    var reply = message.Equals("skip", StringComparison.OrdinalIgnoreCase)
                        ? "Theek hai — jab chahein \"catalog\" likh kar wapas add kar saktay hain."
                        : "🎉 Setup complete — catalog saved. Ab jab bhi order aaye, forward kar dein ya likh dein 'new order: ...'";
                    await ReplyAsync(seller, reply, ct);
                    return;
                }

                var match = ProductLine.Match(message);
                if (match.Success)
                {
                    _db.Products.Add(new Product
                    {
                        SellerId = seller.Id,
                        Name = match.Groups[1].Value.Trim(),
                        Price = decimal.Parse(match.Groups[2].Value)
                    });
                    await ReplyAsync(seller, "✅ Added. Agla? (ya 'done' likhein jab khatam ho)", ct);
                    return;
                }

                // Mid-onboarding interruption (spec screen 17): a recognised command doesn't
                // execute yet — the catalog step must finish (or be explicitly skipped) first.
                if (CommandParser.TryParse(message) is not null)
                {
                    await ReplyAsync(seller,
                        "Abhi koi order nahi hai — pehle catalog complete karein.\n" +
                        "Product name/price bhejein, ya \"skip\" likh kar baad mein karein.", ct);
                    return;
                }

                await ReplyAsync(seller, "Samajh nahi aaya — format: 'naam - price' (e.g. 'Kurti - 1800'), ya 'done'/'skip'.", ct);
                return;

            case ConversationState.OnboardingLanguage:
                seller.PreferredLanguage = Lang.Resolve(message);
                SetState(session, ConversationState.OnboardingBusinessName);
                await ReplyAsync(seller, seller.PreferredLanguage switch
                {
                    Lang.UrduScript => "✅ ٹھیک ہے، اردو میں بات کریں گے۔\n\nاب شروع کرتے ہیں — بزنس کا نام بتائیے؟",
                    Lang.English => "✅ Great, we'll continue in English.\n\nLet's get started — what's your business name?",
                    _ => "✅ Theek hai, Roman Urdu mein baat karenge. (Typed reply bhi chal jata hai, button zaroori nahi)\n\nAb shuru karte hain — business ka naam bataiye?"
                }, ct);
                return;

            default:
                // First touch from a brand-new seller (any message): intro + language picker.
                await StartOnboardingAsync(seller, session, ct);
                return;
        }
    }

    private const string IntroText =
        "👋 Salam! Main aapka Order Assistant hoon.\n\n" +
        "Main aapki madad karta hoon:\n" +
        "📦 Orders record karne mein (Instagram/WhatsApp se forward karein)\n" +
        "📊 Daily/weekly sales dekhne mein\n" +
        "💰 Payment aur COD track karne mein\n" +
        "🎟️ Discount aur loyal customers manage karne mein\n\n" +
        "Sab kuch isi WhatsApp chat mein — koi app install nahi karna.";

    private const string LanguagePromptText = "Pehle language select karein — button dabayein ya khud type karein:";

    private async Task StartOnboardingAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.OnboardingLanguage);
        await ReplyAsync(seller, IntroText, ct);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, LanguagePromptText, Lang.ButtonLabels, ct);
    }
}
