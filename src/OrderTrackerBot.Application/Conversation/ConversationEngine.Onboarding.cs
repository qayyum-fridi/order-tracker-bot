using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private static readonly Regex LooseProductLine = new(@"^(.+?)\s*-\s*(\d+(?:\.\d+)?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InstagramHandle = new(@"@[A-Za-z0-9._]{2,30}", RegexOptions.Compiled);

    // Onboarding accepts any "name - price" (digits allowed in the name, e.g. "Suit 2pc - 3500").
    private static ProductLine? ParseLooseProductLine(string line)
    {
        var m = LooseProductLine.Match(line);
        return m.Success ? CommandParser.SplitUnit(m.Groups[1].Value, decimal.Parse(m.Groups[2].Value)) : null;
    }

    private const string BusinessInfoPrompt =
        "📋 Business details bhejein (comma se alag):\nCity, Business type, @instagram\n\ne.g. \"Lahore, Clothing, @ayesha.collections\" — ya \"cancel\".";

    /// <summary>"Lahore, Clothing, @ayesha.collections" — the @handle can be anywhere; the rest is city then business type.</summary>
    private static void ApplyBusinessInfo(Seller seller, string message)
    {
        var handle = InstagramHandle.Match(message);
        if (handle.Success) seller.InstagramHandle = handle.Value;
        var parts = InstagramHandle.Replace(message, "")
            .Split(new[] { ',', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0).ToList();
        if (parts.Count > 0) seller.City = parts[0];
        if (parts.Count > 1) seller.BusinessType = parts[1];
    }

    private static string BusinessInfoSummary(Seller seller) =>
        string.Join(", ", new[] { seller.City, seller.BusinessType is null ? null : $"{seller.BusinessType} business", seller.InstagramHandle }
            .Where(v => !string.IsNullOrWhiteSpace(v)));

    private async Task HandleBusinessInfoAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        if (CancelWords.Contains(message.Trim()))
        {
            await ReplyAsync(seller, "Theek hai, kuch change nahi kiya.", ct);
            return;
        }
        if (CommandParser.TryParse(message) is { } command)
        {
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
            return;
        }

        ApplyBusinessInfo(seller, message);
        await ReplyAsync(seller, $"✅ Business info update ho gayi — {BusinessInfoSummary(seller)}.", ct);
    }
    private static readonly Regex DoneOrSkip = new(@"^(done|skip)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task HandleOnboardingAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        switch (session.State)
        {
            case ConversationState.OnboardingBusinessName:
                seller.BusinessName = message;
                SetState(session, ConversationState.OnboardingOptionalDetails);
                await ReplyAsync(seller,
                    "Shukriya! Kuch aur details bhi dena chahenge? (optional, skip bhi kar saktay hain)\n" +
                    "📍 City\n🏷️ Business type (e.g. Clothing, Food, Jewelry)\n📸 Instagram handle (agar hai)\n\n" +
                    "Ek ek kar ke bata dein, ya \"skip\" likh kar aage barhein.", ct);
                return;

            case ConversationState.OnboardingOptionalDetails:
                if (CommandParser.TryParse(message) is { Kind: not CommandKind.AddProduct })
                {
                    await ReplyAsync(seller, "Pehle setup complete kar lein — City, business type, @instagram bhejein, ya \"skip\" likhein.", ct);
                    return;
                }
                SetState(session, ConversationState.OnboardingCatalogSize);
                const string askCatalog = "Ab products add karte hain. Kitne products hain — 20 se kam ya zyada?";
                if (DoneOrSkip.IsMatch(message))
                {
                    await ReplyAsync(seller, $"Theek hai — baad mein \"update business info\" se add kar saktay hain.\n\n{askCatalog}", ct);
                    return;
                }
                ApplyBusinessInfo(seller, message);
                await ReplyAsync(seller, $"✅ Noted — {BusinessInfoSummary(seller)}.\n\n{askCatalog}", ct);
                return;

            case ConversationState.OnboardingCatalogSize:
                SetState(session, ConversationState.OnboardingAddProduct);
                var many = message.Contains("zyada", StringComparison.OrdinalIgnoreCase) || message.Contains("more", StringComparison.OrdinalIgnoreCase)
                           || message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => int.TryParse(w, out var n) && n > 20);
                await ReplyAsync(seller, many
                    ? "Theek hai — ek saath kai products paste kar saktay hain, har line mein ek:\nLawn Suit - 3500\nKurti - 1800\nSugar 5 kg - 500\n\n('done' likhein jab khatam ho)"
                    : "Theek hai, ek ek karke bataiye — naam aur price (e.g. 'Lawn Suit - 3500')", ct);
                return;

            case ConversationState.OnboardingAddProduct:
                if (DoneOrSkip.IsMatch(message))
                {
                    seller.OnboardingComplete = true;
                    StartTrial(seller);
                    SetState(session, ConversationState.Idle);
                    var reply = message.Equals("skip", StringComparison.OrdinalIgnoreCase)
                        ? $"Theek hai — jab chahein \"catalog\" likh kar wapas add kar saktay hain.\n{TrialStartedText(seller)}"
                        : $"🎉 Setup complete — catalog saved.{TrialStartedText(seller)}\nAb jab bhi order aaye, forward kar dein ya likh dein 'new order: ...'";
                    await ReplyAsync(seller, reply.TrimEnd(), ct);
                    return;
                }

                var productLines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => CommandParser.TryParseProductLine(l, out ProductLine? p) ? p : ParseLooseProductLine(l))
                    .ToList();
                if (productLines.Count > 0 && productLines.All(p => p is not null))
                {
                    foreach (var line in productLines) await UpsertProductAsync(seller, line!, ct);
                    await ReplyAsync(seller, productLines.Count == 1
                        ? "✅ Added. Agla? (ya 'done' likhein jab khatam ho)"
                        : $"✅ {productLines.Count} products added. Aur? (ya 'done' likhein jab khatam ho)", ct);
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
