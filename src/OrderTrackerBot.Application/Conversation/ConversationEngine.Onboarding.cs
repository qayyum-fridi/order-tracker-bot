using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    private static readonly Regex LooseProductLine = new(@"^(.+?)\s*[-–—:=]\s*(?:rs\.?|pkr)?\s*(\d+(?:\.\d+)?)\s*(?:/-|rs\.?|rupay|rupees|pkr)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex InstagramHandle = new(@"@[A-Za-z0-9._]{2,30}", RegexOptions.Compiled);

    // A Pakistani mobile number in any common spelling (0300-1234567, 03001234567, +92 300 1234567) — its presence
    // means the text is an order/customer detail, never a "name - price" catalog line.
    private static readonly Regex PhoneNumber = new(@"(?<!\d)(?:\+?92[-\s]?|0)3\d{2}[-\s]?\d{7}(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BrokenPriceLine = new(@"^(?<name>[^\d\n:=—–-]{2,40}?)\s*[-–—=:]\s*(?<v>[^\n]*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Trailing "add" instruction a voice rewrite can keep after the price: "Suit - 500, catalog mein add kar dein", "Silk Suit - 800 product ke taur par add kar do".
    private static readonly Regex AddFiller = new(
        @"[\s,;.]*(?:(?:(?:catalog|catalogue|product|products|list)\s+(?:mein|me)|product\s+ke\s+(?:taur|tor)\s+par)\s+)?(?:add|ad)\s+(?:kar\s*)?(?:do|dein|den|karo|kardo|kardein)[\s.!]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // "kurti price 1800": the price word is not part of the product name, so it becomes "kurti - 1800".
    private static readonly Regex PriceWordBeforeAmount = new(
        @"^(.+?)(?:\s+(?:ki|ka|ke|کی|کا|کے))?\s+(?:price|rate|qeemat|keemat|kimat|قیمت|ریٹ)\s*[-–—:=]?\s*(?=(?:rs\.?\s*)?\d[\d.,]*\s*(?:/-|rs\.?|rupay|rupees|pkr)?\s*$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Onboarding accepts any "name - price" (digits allowed in the name, e.g. "Suit 2pc - 3500"), also with ":"/"=" or an Rs/PKR marker.
    internal static ProductLine? ParseLooseProductLine(string line)
    {
        if (PhoneNumber.IsMatch(line)) return null;
        line = CommandParser.NormalizeProductPriceText(line);
        // "address mein" is a mis-heard "catalog mein": it only matters when it trails the price like the other filler.
        line = AddFiller.Replace(Regex.Replace(line, @"[\s,;]+address\s+(?:mein|me)\b", ""), "");
        line = PriceWordBeforeAmount.Replace(line, "$1 - ");
        var m = LooseProductLine.Match(line);
        if (m.Success) return CommandParser.SplitUnit(m.Groups[1].Value, decimal.Parse(m.Groups[2].Value));
        if (CommandParser.TryParseSpacedProductLine(line, out ProductLine? spaced)) return spaced;
        return TryParseNaturalPriceSentence(line);
    }

    // "Suit ki price 3500 rakho", "سوٹ کی قیمت ساڑھے تین ہزار روپے رکھو": a product and its price said as a sentence, not as "naam - price".
    private static readonly Regex NaturalPriceSentence = new(
        @"^\s*(?<name>.+?)\s+(?:ki|ka|ke|کی|کا|کے)\s+(?:price|rate|qeemat|keemat|kimat|قیمت|ریٹ)\s+(?<rest>.+?)\s*[.۔!]*\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NaturalPriceVerb = new(
        @"(?:rakho|rakh\s+do|rakhein|rakhen|rakh\s+dein|hai|hain|set|likho|likh\s+do|kar\s+do|رکھو|رکھیں|رکھ\s+دو|رکھ\s+دیں|ہے|ہیں|لکھو|کر\s+دو)\s*[.۔!]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // "Shawl - teen hazar do sau": the price after the dash written in number words (no digits at all).
    private static readonly Regex SpokenDashPrice = new(@"^(?<name>[^\d]{2,50}?)\s*[-–—:=]\s*(?<rest>[^\d]+?)[.!]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> PriceFiller = new(StringComparer.OrdinalIgnoreCase) { "rupay", "rupaye", "rupees", "rupee", "rs", "pkr", "ka", "ki", "hai", "ke", "روپے", "کا", "ہے" };
    private static readonly Regex WordToken = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Only number words, multipliers (sau, hazar, lakh) and a currency/filler word are allowed after the dash, and they must add up to exactly one amount.</summary>
    private static ProductLine? TryParseSpokenDashPrice(string line)
    {
        var m = SpokenDashPrice.Match(line);
        if (!m.Success || PhoneNumber.IsMatch(line)) return null;
        var rest = m.Groups["rest"].Value;
        var tokens = WordToken.Matches(rest).Select(t => t.Value).ToList();
        if (tokens.Count == 0 || !tokens.All(t => SpokenNumbers.IsNumberWord(t) || SpokenNumbers.IsMultiplier(t) || PriceFiller.Contains(t))) return null;
        var amounts = SpokenNumbers.Amounts(rest);
        var name = m.Groups["name"].Value.Trim();
        return amounts.Count == 1 && name.Length > 0 ? CommandParser.SplitUnit(name, amounts.First()) : null;
    }

    /// <summary>Exactly one amount (digits or spoken words, 100 or more) and a closing verb are required; anything else is not a price sentence.</summary>
    private static ProductLine? TryParseNaturalPriceSentence(string line)
    {
        if (TryParseSpokenDashPrice(line) is { } spoken) return spoken;
        var m = NaturalPriceSentence.Match(line);
        if (!m.Success || PhoneNumber.IsMatch(line)) return null;
        var rest = m.Groups["rest"].Value;
        var amounts = SpokenNumbers.Amounts(rest);
        if (amounts.Count != 1 || !NaturalPriceVerb.IsMatch(rest)) return null;
        var name = m.Groups["name"].Value.Trim();
        if (name.Length == 0 || name.Length > 60) return null;
        return CommandParser.SplitUnit(name, amounts.First());
    }

    /// <summary>
    /// A real order text typed while onboarding is still open ("Ayesha 2 lawn suit aur 1 kurti, 0300-1234567, Lahore, ...").
    /// Instead of rejecting it, treat it as an implicit "skip" of the rest of setup and hand it to the normal order flow —
    /// which already saves the customer and walks through adding unmatched products to the catalog.
    /// <paramref name="requirePhoneHint"/> avoids an AI call on steps where free text is normally something else (business name, city...).
    /// </summary>
    private async Task<bool> TryOrderDuringOnboardingAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message,
        bool requirePhoneHint, CancellationToken ct)
    {
        if (requirePhoneHint && !PhoneNumber.IsMatch(message)) return false;

        var catalog = await LoadCatalogAsync(seller, ct);
        var analysis = await _ai.AnalyzeMessageAsync(AiContext(seller, catalog), message, ct);
        if (analysis.AiUnavailable && PhoneNumber.IsMatch(message))
        {
            // Looks like an order but the AI can't read it right now — say so instead of the misleading format hint.
            await ReplyAsync(seller,
                MessageTemplates.Get("onboarding.001"), ct);
            return true;
        }

        if (!analysis.IsOrderAttempt || analysis.Order is null) return false;

        seller.OnboardingComplete = true;
        StartTrial(seller);
        SetState(session, ConversationState.Idle);
        await ReplyAsync(seller,
            MessageTemplates.Get("onboarding.002", TrialStartedText(seller)), ct);
        await HandleAnalysisAsync(seller, session, ctx, analysis, catalog, fromScreenshot: false, ct);
        return true;
    }

    /// <summary>Saves a buyer named without an order (phone, address and city when said) and returns the reply. An existing customer is left as it is.</summary>
    private async Task<string> SaveNewCustomerAsync(Seller seller, AiNewCustomer buyer, CancellationToken ct)
    {
        var name = buyer.Name.Trim();
        if (await _db.Customers.AnyAsync(c => c.SellerId == seller.Id && c.Name.ToLower() == name.ToLower(), ct))
            return MessageTemplates.Get("onboarding.038", name);


        var phone = buyer.Phone?.Trim();
        _db.Customers.Add(new Customer
        {
            SellerId = seller.Id,
            Name = name,
            Phone = IsPlausiblePhone(phone) ? phone : null,
            Address = buyer.Address?.Trim(),
            City = buyer.City?.Trim()
        });
        await _db.SaveChangesAsync(ct);
        return MessageTemplates.Get("onboarding.037", name);
    }

    private static readonly string BusinessInfoPrompt =
        MessageTemplates.Get("onboarding.022");

    /// <summary>"Lahore, Clothing, @ayesha.collections" — the @handle can be anywhere; the rest is city then business type.</summary>
    private static void ApplyBusinessInfo(Seller seller, string message, bool keepType = false)
    {
        var handle = InstagramHandle.Match(message);
        if (handle.Success) seller.InstagramHandle = handle.Value;
        var parts = InstagramHandle.Replace(message, "")
            .Split(new[] { ',', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0).ToList();
        if (parts.Count > 0) seller.City = parts[0];
        if (parts.Count > 1 && !keepType) seller.BusinessType = parts[1];
    }

    /// <summary>
    /// After the shop city and type: straight to the first product. The "how many products" question is not asked in the MVP;
    /// the product examples come from the seller's kind of shop.
    /// </summary>
    private async Task StartProductStepAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.OnboardingAddProduct);
        await ReplyAsync(seller, MessageTemplates.Get("onboarding.010", BusinessInfoSummary(seller), MessageTemplates.Get("onboarding.021", MessageTemplates.Get("product.formats", Industry(seller).SimpleProduct))), ct);
        await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("onboarding.012"),
            "Options dekhein", CatalogStepQuickActionsFor(), ct);
    }

    private static string BusinessInfoSummary(Seller seller) =>
        string.Join(", ", new[] { seller.City, seller.BusinessType is null ? null : MessageTemplates.Get("onboarding.business", seller.BusinessType), seller.InstagramHandle }
            .Where(v => !string.IsNullOrWhiteSpace(v)));

    private async Task HandleBusinessInfoAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        SetState(session, ConversationState.Idle);
        if (CancelWords.Contains(message.Trim()))
        {
            await ReplyAsync(seller, MessageTemplates.Get("onboarding.003"), ct);
            return;
        }
        if (CommandParser.TryParse(message) is { } command)
        {
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
            return;
        }

        ApplyBusinessInfo(seller, message);
        await ReplyAsync(seller, MessageTemplates.Get("onboarding.004", BusinessInfoSummary(seller), (IndustryProfiles.Resolve(seller.BusinessType) != IndustryProfiles.General ? "\n💡 Is qism ki dukaan ke examples aur tips ke liye \"industry\" likhein." : "")), ct);
    }
    private static readonly Regex DoneOrSkip = new(@"^(done|skip)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "ok", "theek hai", "shukriya": a reply to the last message, not a product or an order.
    private static readonly HashSet<string> AcknowledgementWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ok", "okay", "k", "theek", "theek hai", "thik", "thik hai", "ji", "jee", "acha", "achha", "shukriya", "shukria", "thanks", "thank you"
    };
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

    // Offered right after the catalog-size question, alongside the "enter your first product" prompt — a way
    // out for sellers who aren't ready to type products yet. 4 options need a list (WhatsApp buttons cap at 3).
    // Row ids are real commands, all recognized as escape hatches by the OnboardingAddProduct handler below.
    private static readonly IReadOnlyList<Abstractions.MenuSection> CatalogStepQuickActions = new[]
    {
        new Abstractions.MenuSection("Ya, kuch aur karein", new[]
        {
            new Abstractions.MenuRow("add product", "➕ Product add karein"),
            new Abstractions.MenuRow("setup", "⚙️ Setup"),
            new Abstractions.MenuRow("new order", "📦 New Order"),
            new Abstractions.MenuRow("guide", "📖 Guide"),
            new Abstractions.MenuRow("connect instagram", "📷 Instagram Connect")
        })
    };

    /// <summary>The "connect instagram" row is left out while Features:Instagram is off.</summary>
    private IReadOnlyList<Abstractions.MenuSection> CatalogStepQuickActionsFor() => _features.Instagram
        ? CatalogStepQuickActions
        : CatalogStepQuickActions
            .Select(s => new Abstractions.MenuSection(s.Title, s.Rows.Where(r => r.Id != "connect instagram").ToArray()))
            .ToArray();

    /// <summary>After each product: the confirmation and the next-step buttons in one message, so the options sit right under it.</summary>
    private Task SendAddProductChoicesAsync(Seller seller, string text, CancellationToken ct) =>
        _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
            MessageTemplates.Get("onboarding.005", text), AddProductChoices, ct);

    /// <summary>A tapped button's label without emoji/punctuation, lower-cased ("✅ Done" -> "done").</summary>
    private static string ButtonWords(string message) =>
        Regex.Replace(message, @"[^\p{L}\p{N} ]", "").Trim().ToLowerInvariant();
    private static readonly string[] ManyProductsWords =
        { "zyada", "ziyada", "bohat", "bohot", "bahut", "kafi", "many", "more", "a lot", "lots", "bara", "bara (20+)", "20+" };

    private async Task HandleOnboardingAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        // "meri dukaan ka naam hai X" is a name, not an answer: a correction after the name is set keeps the question open.
        if (session.State is ConversationState.OnboardingIndustry or ConversationState.OnboardingOptionalDetails
            && CommandParser.TryParseShopName(message, out var renamed))
        {
            seller.BusinessName = renamed;
            await ReplyAsync(seller, MessageTemplates.Get("onboarding.200", renamed), ct);
            if (session.State == ConversationState.OnboardingIndustry) await AskIndustryAsync(seller, null, ct);
            return;
        }

        switch (session.State)
        {
            case ConversationState.OnboardingBusinessName:
                if (await TryOnboardingShortcutAsync(seller, session, message, ct)) return;
                if (await TryOrderDuringOnboardingAsync(seller, session, ctx, message, requirePhoneHint: true, ct)) return;
                // A product line is not a shop name: ask for the name again instead of saving "Kurti - 1800" as the shop.
                if (CommandParser.TryParseProductLine(message, out _))
                {
                    await ReplyAsync(seller, MessageTemplates.Get("onboarding.033"), ct);
                    return;
                }
                seller.BusinessName = CommandParser.TryParseShopName(message, out var typedName) ? typedName : message;
                // The city comes first, then the kind of shop (taps); the Instagram handle is not asked in the MVP.
                SetState(session, ConversationState.OnboardingOptionalDetails);
                // Skip is a tap, not a word to type. A real order sent at any step also ends setup (TryOrderDuringOnboardingAsync), and the seller is told so.
                await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
                    MessageTemplates.Get("onboarding.006"), new[] { "Skip" }, ct);
                return;

            case ConversationState.OnboardingIndustry:
            {
                if (CommandParser.TryParse(message) is { Kind: CommandKind.Help or CommandKind.Menu } helpCmd)
                {
                    await ExecuteCommandAsync(seller, session, ctx, helpCmd, ct);
                    return;
                }
                if (await TryOrderDuringOnboardingAsync(seller, session, ctx, message, requirePhoneHint: true, ct)) return;
                var picked = PickIndustry(message);
                if (picked is null)
                {
                    // A real command here (e.g. "orders today") runs only after setup: say so, then show the list again.
                    if (CommandParser.TryParse(message) is { Kind: not (CommandKind.Help or CommandKind.Menu) })
                    {
                        await ReplyAsync(seller, MessageTemplates.Get("onboarding.036"), ct);
                        await AskIndustryAsync(seller, null, ct);
                        return;
                    }
                    // A product line here means the seller skipped the shop type: say what is expected first, then show the list again.
                    if (CommandParser.TryParseProductLine(message, out _))
                    {
                        await ReplyAsync(seller, MessageTemplates.Get("onboarding.034"), ct);
                        await AskIndustryAsync(seller, null, ct);
                        return;
                    }
                    await AskIndustryAsync(seller, message.Trim(), ct);
                    return;
                }
                seller.BusinessType = picked.Title;
                await _db.SaveChangesAsync(ct);
                await StartProductStepAsync(seller, session, ct);
                return;
            }

            case ConversationState.OnboardingOptionalDetails:
                if (CommandParser.TryParse(message) is { Kind: not CommandKind.AddProduct } parsedOptional)
                {
                    if (parsedOptional.Kind is CommandKind.Help or CommandKind.Menu or CommandKind.ConnectInstagram)
                    {
                        await ExecuteCommandAsync(seller, session, ctx, parsedOptional, ct);
                        return;
                    }

                    await ReplyAsync(seller, MessageTemplates.Get("onboarding.008"), ct);
                    return;
                }
                if (await TryOrderDuringOnboardingAsync(seller, session, ctx, message, requirePhoneHint: true, ct)) return;
                // A product line is not the city or Instagram handle: keep this step open and say what it expects.
                if (CommandParser.TryParseProductLine(message, out _))
                {
                    await ReplyAsync(seller, MessageTemplates.Get("onboarding.035"), ct);
                    return;
                }
                // The city was the answer here; the kind of shop comes next as taps, then the first product (no product-count question).
                if (!DoneOrSkip.IsMatch(message)) ApplyBusinessInfo(seller, message, keepType: true);
                if (_features.IndustryQuestion)
                {
                    SetState(session, ConversationState.OnboardingIndustry);
                    await AskIndustryAsync(seller, null, ct);
                    return;
                }
                await StartProductStepAsync(seller, session, ct);
                return;

            case ConversationState.OnboardingCatalogSize:
                if (await TryOrderDuringOnboardingAsync(seller, session, ctx, message, requirePhoneHint: true, ct)) return;
                SetState(session, ConversationState.OnboardingAddProduct);
                var many = ManyProductsWords.Any(w => message.Contains(w, StringComparison.OrdinalIgnoreCase))
                           || message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => int.TryParse(w, out var n) && n > 20);
                await ReplyAsync(seller, many
                    ? MessageTemplates.Get("onboarding.020", string.Join("\n", Industry(seller).ProductLines))
                    : MessageTemplates.Get("onboarding.021", Industry(seller).SimpleProduct), ct);
                await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("onboarding.012"),
                    "Options dekhein", CatalogStepQuickActionsFor(), ct);
                return;

            case ConversationState.OnboardingAddProduct:
                if (DoneOrSkip.IsMatch(message) || ButtonWords(message) == "done")
                {
                    seller.OnboardingComplete = true;
                    StartTrial(seller);
                    SetState(session, ConversationState.Idle);
                    var reply = message.Equals("skip", StringComparison.OrdinalIgnoreCase)
                        ? MessageTemplates.Get("onboarding.024", TrialStartedText(seller))
                        : MessageTemplates.Get("onboarding.025", TrialStartedText(seller));
                    await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("onboarding.013", reply.TrimEnd()),
                        "Options dekhein", AfterSetupSections, ct);
                    await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber,
                        MessageTemplates.Get("onboarding.014"), GuideOfferButtons, ct);
                    return;
                }

                var choice = ButtonWords(message);
                if (choice is "add another" or "aur product" or "add more" or "aur")
                {
                    await ReplyAsync(seller, MessageTemplates.Get("onboarding.015", Industry(seller).SimpleProduct), ct);
                    return;
                }

                if (choice is "catalog dekhein" or "catalog")
                {
                    var catalog = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Id).ToListAsync(ct);
                    var list = catalog.Count == 0
                        ? MessageTemplates.Get("onboarding.026")
                        : MessageTemplates.Get("onboarding.027", catalog.Count, string.Join("\n", catalog.Select((p, i) => $"{i + 1}. {Formatters.ProductLabel(p)} - {Formatters.Money(p.Price)}")));
                    await SendAddProductChoicesAsync(seller, list, ct);
                    return;
                }

                var productLines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => CommandParser.TryParseProductLine(l, out ProductLine? p) ? p : ParseLooseProductLine(l))
                    .ToList();
                if (productLines.Count > 0 && productLines.All(p => p is not null))
                {
                    // One product line is shown back for YES first (saved by TryHandlePendingProductAsync).
                    if (_features.ProductConfirmation && productLines.Count == 1)
                    {
                        await AskProductConfirmationAsync(seller, ctx, new[] { productLines[0]! }, ct);
                        return;
                    }
                    var added = new List<Product>();
                    foreach (var line in productLines)
                        if ((await UpsertProductAsync(seller, line!, ct)).Product is { } listed) added.Add(listed);
                    await _db.SaveChangesAsync(ct);
                    var total = await _db.Products.CountAsync(p => p.SellerId == seller.Id && p.IsActive, ct);
                    var summary = added.Count == 1
                        ? MessageTemplates.Get("onboarding.028", Formatters.ProductLabel(added[0]), Formatters.Money(added[0].Price), ProductDetailsText(added[0]))
                        : MessageTemplates.Get("onboarding.029", added.Count, string.Join("\n", added.Select(p => $"• {Formatters.ProductLabel(p)} - {Formatters.Money(p.Price)}{ProductDetailsText(p)}")));
                    await SendAddProductChoicesAsync(seller, $"{summary}\n\nCatalog mein ab {total} product{(total == 1 ? "" : "s")} hain.", ct);
                    return;
                }

                // Mid-onboarding interruption (spec screen 17): a recognised command doesn't
                // execute yet — the catalog step must finish (or be explicitly skipped) first.
                // Escape-hatch commands (help/menu/connect instagram/setup/new order/guide) still
                // work since they can't corrupt onboarding state and the seller needs a way out
                // if stuck — Guide is state-preserving too (see StartGuideAsync/ExitGuide).
                if (CommandParser.TryParse(message) is { } parsed)
                {
                    if (parsed.Kind is CommandKind.Help or CommandKind.Menu or CommandKind.ConnectInstagram or CommandKind.ImportCatalogSheet
                        or CommandKind.BusinessSetup or CommandKind.NewOrderHelp or CommandKind.Guide or CommandKind.Stock
                        or CommandKind.ImportHelp or CommandKind.ImportTemplate or CommandKind.HowTo
                        or CommandKind.CustomerSearch or CommandKind.OrderSearch or CommandKind.CustomerOrderLookup
                        // Read-only lookups: they change nothing, so the seller can check orders or reports during setup.
                        or CommandKind.OrdersToday or CommandKind.OrderPage or CommandKind.PendingOrders or CommandKind.UnpaidOrders
                        or CommandKind.TodaysSummary or CommandKind.Profit or CommandKind.Margin or CommandKind.WeeklySummary
                        or CommandKind.CustomerList or CommandKind.MenuCategory)
                    {
                        await ExecuteCommandAsync(seller, session, ctx, parsed, ct);
                        return;
                    }

                    await ReplyAsync(seller,
                        MessageTemplates.Get("onboarding.016"), ct);
                    return;
                }

                // Not a product line or command: if it's really a customer order (it names a buyer phone), take it instead of rejecting it.
                // Without a phone, free text here is the seller describing their own stock ("mere paas 4 lawn suit hain, 3500"), never an order.
                if (await TryOrderDuringOnboardingAsync(seller, session, ctx, message, requirePhoneHint: true, ct)) return;

                if (AcknowledgementWords.Contains(ButtonWords(message)))
                {
                    await ReplyAsync(seller, MessageTemplates.Get("onboarding.017", Industry(seller).SimpleProduct), ct);
                    return;
                }

                // "teen chadar aur do dupatte naye products hain": the seller is naming products (maybe without prices) — read it and ask for the prices.
                var stockAnalysis = await _ai.AnalyzeMessageAsync(AiContext(seller, await LoadCatalogAsync(seller, ct), ctx), message, ct);
                // "naya client Muhammad Ali, address Model Town Lahore": a buyer to save before any order (no items, so not an order).
                if (stockAnalysis is { Intent: "add_customer", NewCustomer: { } buyer })
                {
                    await SendAddProductChoicesAsync(seller, await SaveNewCustomerAsync(seller, buyer, ct), ct);
                    return;
                }

                if (stockAnalysis is { Intent: "add_products", NewProducts.Count: > 0 })
                {
                    await HandleNewProductsAsync(seller, ctx, stockAnalysis.NewProducts, message, ct);
                    return;
                }

                // "Handbag - abc" / "Scarf -": it is clearly a product line, so say which part could not be read instead of a generic failure.
                if (BrokenPriceLine.Match(message.Trim()) is { Success: true } broken && !PhoneNumber.IsMatch(message) && broken.Groups["name"].Value.Trim().Split(' ').Length <= 4)
                {
                    var productName = broken.Groups["name"].Value.Trim();
                    var wrote = broken.Groups["v"].Value.Trim();
                    await ReplyAsync(seller,
                        MessageTemplates.Get("onboarding.018", productName, (wrote.Length == 0 ? " (price likhi nahi)" : $" (\"{wrote}\")"), productName), ct);
                    return;
                }

                await ReplyAsync(seller,
                    MessageTemplates.Get("onboarding.019", Industry(seller).SimpleProduct), ct);
                return;

            case ConversationState.OnboardingLanguage:
                await OnboardingLanguageChosenAsync(seller, session, message, ct);
                return;

            case ConversationState.OnboardingStartChoice:
                await HandleOnboardingStartChoiceAsync(seller, session, message, ct);
                return;

            default:
                // First touch from a brand-new seller (any message): intro + language picker.
                await StartOnboardingAsync(seller, session, ct);
                return;
        }
    }

    // Shown before the seller has chosen a language, so it's always in Urdu script regardless of their eventual preference.
    private static readonly string IntroText =
        MessageTemplates.Get("onboarding.030");

    private static readonly string LanguagePromptText = MessageTemplates.Get("onboarding.031");

    private async Task StartOnboardingAsync(Seller seller, ConversationSession session, CancellationToken ct)
    {
        SetState(session, ConversationState.OnboardingLanguage);
        await ReplyAsync(seller, IntroText, ct);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, LanguagePromptText, Lang.ButtonLabels, ct);
    }
}
