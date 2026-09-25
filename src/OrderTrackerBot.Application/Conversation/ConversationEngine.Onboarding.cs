using Microsoft.EntityFrameworkCore;
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
            await ReplyAsync(seller, "Ji theek hai, koi tabdeeli nahi ki gayi.", ct);
            return;
        }
        if (CommandParser.TryParse(message) is { } command)
        {
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
            return;
        }

        ApplyBusinessInfo(seller, message);
        await ReplyAsync(seller, $"✅ Business info update ho gayi, shukriya — {BusinessInfoSummary(seller)}.", ct);
    }
    private static readonly Regex DoneOrSkip = new(@"^(done|skip)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] AddProductChoices = { "➕ Aur product", "📋 Catalog dekhein", "✅ Done" };

    // Row ids are real commands — a tapped row arrives as that text.
    private static readonly IReadOnlyList<Abstractions.MenuSection> AfterSetupSections = new[]
    {
        new Abstractions.MenuSection("Shuru karein", new[]
        {
            new Abstractions.MenuRow("new order (detailed)", "📦 Pehla order darj karein"),
            new Abstractions.MenuRow("catalog", "🛍️ Catalog dekhein"),
            new Abstractions.MenuRow("add payment", "💰 Payment number add"),
            new Abstractions.MenuRow("menu", "📋 Main menu"),
            new Abstractions.MenuRow("help", "🆘 Help")
        })
    };

    /// <summary>After each product: the confirmation and the next-step buttons in one message, so the options sit right under it.</summary>
    private Task SendAddProductChoicesAsync(Seller seller, string text, CancellationToken ct) =>
        _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            $"{text}\n\nAb kya karna chahte hain? Neeche button dabayein, ya seedha agla product likh dein.", AddProductChoices, ct);

    /// <summary>A tapped button's label without emoji/punctuation, lower-cased ("✅ Done" -> "done").</summary>
    private static string ButtonWords(string message) =>
        Regex.Replace(message, @"[^\p{L}\p{N} ]", "").Trim().ToLowerInvariant();
    private static readonly string[] ManyProductsWords =
        { "zyada", "ziyada", "bohat", "bohot", "bahut", "kafi", "many", "more", "a lot", "lots", "bara", "bara (20+)", "20+" };

    private async Task HandleOnboardingAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        switch (session.State)
        {
            case ConversationState.OnboardingBusinessName:
                seller.BusinessName = message;
                SetState(session, ConversationState.OnboardingOptionalDetails);
                await ReplyAsync(seller,
                    "Shukriya! Kya aap kuch mazeed tafseelat bhi dena chahenge? (ikhtiyari hai, \"skip\" bhi kar saktay hain)\n" +
                    "📍 Shehar\n🏷️ Karobar ki qisam (jaise Clothing, Food, Jewelry)\n📸 Instagram handle (agar mojood ho)\n\n" +
                    "Baari baari bata dein, ya aage barhne ke liye \"skip\" likhein.", ct);
                return;

            case ConversationState.OnboardingOptionalDetails:
                if (CommandParser.TryParse(message) is { Kind: not CommandKind.AddProduct })
                {
                    await ReplyAsync(seller, "Meharbani kar ke pehle setup mukammal karein — shehar, karobar ki qisam ya @instagram handle bhejein, ya \"skip\" likhein.", ct);
                    return;
                }
                SetState(session, ConversationState.OnboardingCatalogSize);
                const string askCatalog = "Ab products add karte hain. Neeche button dabayein, ya seedha number likh dein.";
                if (DoneOrSkip.IsMatch(message))
                {
                    await ReplyAsync(seller, $"Ji theek hai — yeh tafseelat aap baad mein \"update business info\" likh kar add kar saktay hain.\n\n{askCatalog}", ct);
                }
                else
                {
                    ApplyBusinessInfo(seller, message);
                    await ReplyAsync(seller, $"✅ Noted — {BusinessInfoSummary(seller)}. Shukriya!\n\n{askCatalog}", ct);
                }
                await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, "Aapke paas kitne products hain?",
                    new[] { "Chhota (20 se kam)", "Bara (20+)" }, ct);
                return;

            case ConversationState.OnboardingCatalogSize:
                SetState(session, ConversationState.OnboardingAddProduct);
                var many = ManyProductsWords.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase))
                           || message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => int.TryParse(w, out var n) && n > 20);
                await ReplyAsync(seller, many
                    ? "Bohot khoob — aap ek saath kai products bhi bhej saktay hain, har line mein aik product likhein:\nLawn Suit - 3500\nKurti - 1800\nSugar 5 kg - 500\n\nJab tamam products add ho jayein to \"done\" likh dein."
                    : "Theek hai, barah-e-meharbani ek ek karke product ka naam aur price bataein — misaal ke taur par 'Lawn Suit - 3500'.", ct);
                return;

            case ConversationState.OnboardingAddProduct:
                if (DoneOrSkip.IsMatch(message) || ButtonWords(message) == "done")
                {
                    seller.OnboardingComplete = true;
                    StartTrial(seller);
                    SetState(session, ConversationState.Idle);
                    var reply = message.Equals("skip", StringComparison.OrdinalIgnoreCase)
                        ? $"Ji theek hai — jab bhi chahein \"catalog\" likh kar dobara products add kar saktay hain.\n{TrialStartedText(seller)}"
                        : $"🎉 Mubarak ho, aapka setup mukammal ho gaya aur catalog save ho gayi hai.{TrialStartedText(seller)}\nAb jab bhi koi order aaye, usay forward kar dein ya 'new order: ...' likh kar darj karein.";
                    await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, $"{reply.TrimEnd()}\n\nAage kya karna hai? Neeche se chunein 👇",
                        "Options dekhein", AfterSetupSections, ct);
                    await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
                        "👉 2 minute ka quick guide dekhna chahenge? Sab kuch samajh aa jayega, ek tap mein.", GuideOfferButtons, ct);
                    return;
                }

                var choice = ButtonWords(message);
                if (choice is "add another" or "aur product" or "add more" or "aur")
                {
                    await ReplyAsync(seller, "Theek hai, agle product ka naam aur price likh dein (jaise 'Kurti - 1800').", ct);
                    return;
                }

                if (choice is "catalog dekhein" or "catalog")
                {
                    var catalog = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Id).ToListAsync(ct);
                    var list = catalog.Count == 0
                        ? "Abhi catalog khali hai."
                        : $"🛍️ Aapka Catalog ({catalog.Count}):\n" + string.Join("\n", catalog.Select((p, i) => $"{i + 1}. {Formatters.ProductLabel(p)} - {Formatters.Money(p.Price)}"));
                    await SendAddProductChoicesAsync(seller, list, ct);
                    return;
                }

                var productLines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => CommandParser.TryParseProductLine(l, out ProductLine? p) ? p : ParseLooseProductLine(l))
                    .ToList();
                if (productLines.Count > 0 && productLines.All(p => p is not null))
                {
                    var added = new List<Product>();
                    foreach (var line in productLines) added.Add((await UpsertProductAsync(seller, line!, ct)).Product);
                    await _db.SaveChangesAsync(ct);
                    var total = await _db.Products.CountAsync(p => p.SellerId == seller.Id && p.IsActive, ct);
                    var summary = added.Count == 1
                        ? $"✅ Add ho gaya: {Formatters.ProductLabel(added[0])} - {Formatters.Money(added[0].Price)}"
                        : $"✅ {added.Count} products add ho gaye:\n" + string.Join("\n", added.Select(p => $"• {Formatters.ProductLabel(p)} - {Formatters.Money(p.Price)}"));
                    await SendAddProductChoicesAsync(seller, $"{summary}\n\nCatalog mein ab {total} product{(total == 1 ? "" : "s")} hain.", ct);
                    return;
                }

                // Mid-onboarding interruption (spec screen 17): a recognised command doesn't
                // execute yet — the catalog step must finish (or be explicitly skipped) first.
                if (CommandParser.TryParse(message) is not null)
                {
                    await ReplyAsync(seller,
                        "Filhaal koi order darj nahi ho sakta — pehle catalog complete karein.\n" +
                        "Product ka naam aur price bhejein, ya \"skip\" likh kar yeh marhala baad mein mukammal karein.", ct);
                    return;
                }

                await ReplyAsync(seller, "Maazrat, samajh nahi aaya. Format yeh hai: 'naam - price' (misaal: 'Kurti - 1800'), ya 'done'/'skip' likhein.", ct);
                return;

            case ConversationState.OnboardingLanguage:
                seller.PreferredLanguage = Lang.Resolve(message);
                SetState(session, ConversationState.OnboardingBusinessName);
                await ReplyAsync(seller, seller.PreferredLanguage switch
                {
                    Lang.UrduScript => "✅ بہت اچھا، ہم اردو میں بات کریں گے۔\n\nآئیے شروع کرتے ہیں — براہ کرم اپنے کاروبار کا نام بتائیں؟",
                    Lang.English => "✅ Wonderful, we'll continue in English.\n\nLet's get started — could you please share your business name?",
                    _ => "✅ Bohot khoob, hum Roman Urdu mein baat karenge. (Aap type kar ke bhi jawab de saktay hain, button zaroori nahi)\n\nAayein shuru karte hain — barah-e-meharbani apne karobar ka naam bataein?"
                }, ct);
                return;

            default:
                // First touch from a brand-new seller (any message): intro + language picker.
                await StartOnboardingAsync(seller, session, ct);
                return;
        }
    }

    // Shown before the seller has chosen a language, so it's always in Urdu script regardless of their eventual preference.
    private const string IntroText =
        "👋 السلام علیکم! میں آپ کا Order Assistant ہوں۔\n\n" +
        "میں آپ کی درج ذیل کاموں میں مدد کرتا ہوں:\n" +
        "📦 آرڈرز ریکارڈ کرنے میں (انسٹاگرام/واٹس ایپ سے فارورڈ کریں)\n" +
        "📊 روزانہ اور ہفتہ وار سیلز دیکھنے میں\n" +
        "💰 ادائیگی اور COD ٹریک کرنے میں\n" +
        "🎟️ ڈسکاؤنٹ اور وفادار گاہکوں کا انتظام کرنے میں\n\n" +
        "یہ سب کچھ اسی واٹس ایپ چیٹ میں ہو جاتا ہے — کسی الگ ایپ کی ضرورت نہیں۔";

    private const string LanguagePromptText = "سب سے پہلے اپنی پسندیدہ زبان منتخب کریں — نیچے بٹن دبائیں یا خود لکھ دیں:";

    private async Task StartOnboardingAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.OnboardingLanguage);
        await ReplyAsync(seller, IntroText, ct);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, LanguagePromptText, Lang.ButtonLabels, ct);
    }
}
