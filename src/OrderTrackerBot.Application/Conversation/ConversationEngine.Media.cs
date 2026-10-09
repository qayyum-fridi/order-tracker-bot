using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Screens 5d-2..5d-4: a forwarded screenshot is either an order (Instagram/TikTok DM) or a payment receipt.
public partial class ConversationEngine
{
    private static readonly Regex OrderRef = new(@"#?(\d+)", RegexOptions.Compiled);

    private static string MediaLimitText(MediaKind kind, int limit, TimeSpan wait)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return kind == MediaKind.Voice
            ? $"🎤 Ek ghante mein {limit} voice notes ki limit poori ho gayi.\n{minutes} minute baad dobara bhejein, ya abhi text mein likh dein."
            : $"📷 Ek ghante mein {limit} screenshots ki limit poori ho gayi.\n{minutes} minute baad dobara bhejein, ya abhi order text mein likh dein.";
    }

    /// <summary>Voice note: transcribe, echo what was heard so the seller can catch mistakes, then handle it exactly like typed text.</summary>
    public async Task HandleAudioMessageAsync(string fromPhoneNumber, string mediaId, CancellationToken ct = default)
    {
        if (_transcriber is not { IsConfigured: true })
        {
            await HandleUnsupportedMediaAsync(fromPhoneNumber, "audio", ct);
            return;
        }

        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        if (_mediaLimiter is { } voiceLimiter && !voiceLimiter.TryConsume(fromPhoneNumber, MediaKind.Voice, out var voiceWait))
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber, MediaLimitText(MediaKind.Voice, voiceLimiter.LimitFor(MediaKind.Voice), voiceWait), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var media = _media is null ? null : await _media.DownloadAsync(mediaId, ct);
        var vocabulary = media is null ? null : await BuildVoiceVocabularyAsync(seller, ct);
        var text = media is null ? null : await _transcriber.TranscribeAsync(media.Value.Bytes, media.Value.MimeType, vocabulary, ct);
        if (text is null)
        {
            if (_issues is not null)
                await _issues.ReportAsync(Abstractions.IssueCodes.VoiceTranscriptionFailed, fromPhoneNumber,
                    media is null ? "media download failed" : "transcription returned nothing", null, ct);
            await _sender.SendTextMessageAsync(fromPhoneNumber,
                "🎤 Voice message samajh nahi aaya — dobara saaf bol kar bhejein, ya likh kar bhej dein.", ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var (steps, question, options) = await InterpretVoiceAsync(seller, text, ct);
        var heard = $"🎤 Maine suna: \"{text}\"";
        if (question is not null)
        {
            // Clear intent but a detail is missing (which order, the new price): ask for it instead of guessing or failing.
            // When the answers are a few known choices they come as tap buttons, so the seller need not speak again.
            await SendChoicesAsync(fromPhoneNumber, $"{heard}\n\n❓ {question}", options.Select(o => new ChoiceOption(o, o)).ToList(), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // City / business type / handle are one answer ("Lahore, Clothing, @x"); separate steps would leave the first one to
        // complete the question and push the rest into the next onboarding step.
        if (steps.Count > 1 && seller.Session!.State == ConversationState.OnboardingOptionalDetails)
            steps = new[] { string.Join(", ", steps) };

        // "done"/"skip" end setup and start the trial: the rewrite must not add them unless the seller said they were finished.
        if (!seller.OnboardingComplete && !FinishWords.IsMatch(text) && steps.Any(s => DoneOrSkip.IsMatch(s.Trim())))
        {
            var withoutFinish = steps.Where(s => !DoneOrSkip.IsMatch(s.Trim())).ToList();
            steps = withoutFinish.Count > 0 ? withoutFinish : new List<string> { text };
        }

        if (steps.Count != 1 || !string.Equals(steps[0], text, StringComparison.OrdinalIgnoreCase))
            heard += $"\n➡️ Samjha: {string.Join("  →  ", steps.Select(s => $"\"{s}\""))}";

        // Money-changing or hard-to-reverse actions (price edit, cancel, status change...) wait for a YES before anything runs.
        if (ShouldConfirmVoiceSteps(seller, steps))
        {
            var session = seller.Session!;
            var ctx = SessionContextData.FromJson(session.ContextJson);
            ctx.PendingVoiceSteps = steps.ToList();
            SetState(session, ConversationState.AwaitingVoiceConfirmation);
            await _sender.SendTextMessageAsync(fromPhoneNumber, $"{heard}\n\n⚠️ Yeh karoon? Reply YES ya NO.", ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        await _sender.SendTextMessageAsync(fromPhoneNumber, heard, ct);
        await RunVoiceStepsAsync(fromPhoneNumber, steps, ct);
    }

    /// <summary>Words that really mean "I am finished / skip this" in the transcript (Roman Urdu, English or Urdu script).</summary>
    private static readonly Regex FinishWords = new(
        @"\b(?:done|skip|finish(?:ed)?|complete|khatam|khtm|bas|hogaya|ho\s+gaya|mukammal|chor|chhor|baad\s+mein)\b|ڈن|ختم|بس|ہو\s*گیا|مکمل|سکپ|چھوڑ|بعد\s*میں",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The seller's own words (shop, customers, products) given to the speech model so names are heard correctly.</summary>
    private async Task<List<string>> BuildVoiceVocabularyAsync(Seller seller, CancellationToken ct)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(seller.BusinessName)) names.Add(seller.BusinessName);
        names.AddRange(await _db.Customers.Where(c => c.SellerId == seller.Id && c.DeletedAt == null)
            .OrderByDescending(c => c.Id).Select(c => c.Name).Take(40).ToListAsync(ct));
        names.AddRange((await LoadCatalogAsync(seller, ct)).Take(30).Select(c => c.Label));
        return names;
    }

    /// <summary>
    /// One AI call that rewrites the transcript into what the seller would have typed for the bot's current question
    /// ("pehla wala" -> "1", "haan kar do" -> "yes", "mere paas 4 lawn suit 3500" -> "Lawn Suit - 3500"). The rewrite still goes through the
    /// normal deterministic engine; the transcript itself is used when the AI is unavailable or the rewrite drops a number.
    /// </summary>
    private async Task<(IReadOnlyList<string> Steps, string? Question, IReadOnlyList<string> Options)> InterpretVoiceAsync(Seller seller, string transcript, CancellationToken ct)
    {
        var ctx = SessionContextData.FromJson(seller.Session!.ContextJson);
        var catalog = await LoadCatalogAsync(seller, ct);
        var recent = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id).OrderByDescending(o => o.Id).Take(5).ToListAsync(ct);
        var customers = await _db.Customers.Include(c => c.Orders)
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null).ToListAsync(ct);
        var knownCustomers = customers
            .OrderByDescending(c => c.Orders.Select(o => o.Id).DefaultIfEmpty(0).Max()).ThenByDescending(c => c.Id).Take(40)
            .Select(DescribeCustomerForVoice).ToList();
        var lastMessages = await _db.MessageLogs.Where(m => m.Phone == seller.WhatsAppPhoneNumber)
            .OrderByDescending(m => m.Id).Take(6).ToListAsync(ct);
        var exchanges = lastMessages.AsEnumerable().Reverse()
            .Select(m => $"{(m.Direction == "outbound" ? "Bot" : "Seller")}: {(m.RawText.Length > 300 ? m.RawText[..300] : m.RawText)}").ToList();

        var result = await _ai.InterpretVoiceAsync(new AiVoiceContext
        {
            BusinessName = seller.BusinessName ?? "",
            Situation = DescribeVoiceSituation(seller, ctx),
            AllowedActions = AllowedVoiceActions(seller, seller.Session!.State).ToList(),
            CatalogNames = catalog.Select(c => c.Label).ToList(),
            RecentOrders = recent.Select(DescribeOrderForVoice).ToList(),
            KnownCustomers = knownCustomers,
            RecentExchanges = exchanges
        }, transcript, ct);

        if (result?.Question is { } question && result.Steps.Count == 0) return (Array.Empty<string>(), question, result.Options);
        if (result is { Steps.Count: > 0 })
        {
            // The model may only pick actions that make sense right now; anything else it made up is dropped (and the transcript is used if nothing is left).
            var steps = result.Actions.Count == result.Steps.Count
                ? ValidateVoiceSteps(seller.Session!.State, seller, result.Steps, result.Actions)
                : result.Steps.Select(s => s.Trim()).ToList();
            if (steps.Count > 0 && IsFaithfulRewrite(transcript, string.Join("\n", steps), KnownVoiceNumbers(recent, catalog))) return (steps, null, Array.Empty<string>());
        }
        return (new[] { transcript }, null, Array.Empty<string>());
    }

    /// <summary>Ids, totals and prices the model was shown for this voice note: a rewrite may legitimately use them ("pichla order" -> "edit order 105").</summary>
    private static IReadOnlySet<long> KnownVoiceNumbers(IEnumerable<Order> recent, IEnumerable<CatalogEntry> catalog)
    {
        var known = new HashSet<long>();
        void Add(decimal value) { if (value == decimal.Truncate(value) && value >= 0) known.Add((long)value); }
        foreach (var order in recent)
        {
            Add(order.Id);
            Add(order.Total);
            foreach (var item in order.Items) Add(item.UnitPrice);
        }
        foreach (var entry in catalog) Add(entry.Product.Price);
        return known;
    }

    private static string DescribeCustomerForVoice(Customer c)
    {
        var active = c.Orders.Where(o => o.Status is not (OrderStatus.Cancelled or OrderStatus.Returned)).OrderByDescending(o => o.Id).ToList();
        var history = active.Count == 0 ? "no orders yet" : $"{active.Count} order{(active.Count == 1 ? "" : "s")}, last Order #{active[0].Id} {active[0].Status}";
        return $"{c.Name}, {c.Phone ?? "no phone"}{(string.IsNullOrWhiteSpace(c.City) ? "" : ", " + c.City)}: {history}";
    }

    private static string DescribeOrderForVoice(Order o) =>
        $"Order #{o.Id} {o.Customer?.Name}, {o.Status}: " +
        string.Join("; ", o.Items.Select((i, n) => $"{n + 1}) {i.ProductNameSnapshot} x{i.Quantity} @ Rs.{i.UnitPrice:0.##}")) + $" — total Rs.{o.Total:0.##}";

    /// <summary>
    /// A rewrite is only trusted if it is not absurdly long and its amounts match what the seller said, in both directions:
    /// (1) every price/phone-sized run of 3+ digits the transcript has survives; (2) every amount the transcript spells out in words
    /// ("teen sau", "three thousand", "تین ہزار") shows up in the rewrite, as digits or words; (3) the rewrite invents no amount (100-999,999)
    /// that the seller did not say — except <paramref name="knownNumbers"/>, ids and prices the model was shown (so "last order" can become "edit order 105").
    /// Small numbers may change on purpose ("4 lawn suits at 3500" -> "Lawn Suit - 3500", "pehla" -> "1"); the "Samjha" echo shows the result.
    /// An amount the seller took back ("410 nahi, 420") need not survive. Phone-length digit runs (7+) are only protected by rule (1).
    /// </summary>
    public static bool IsFaithfulRewrite(string transcript, string? rewritten, IReadOnlySet<long>? knownNumbers = null) =>
        WhyUnfaithful(transcript, rewritten, knownNumbers) is null;

    /// <summary>The first guard rule a rewrite breaks, in words, or null when the rewrite is faithful (same rules as <see cref="IsFaithfulRewrite"/>).</summary>
    public static string? WhyUnfaithful(string transcript, string? rewritten, IReadOnlySet<long>? knownNumbers = null)
    {
        if (string.IsNullOrWhiteSpace(rewritten)) return "empty rewrite";
        if (rewritten.Length > Math.Max(200, transcript.Length * 3)) return "rewrite too long";
        // Amounts the seller took back ("410 nahi, 420") need not survive the rewrite.
        var retracted = SpokenNumbers.RetractedAmounts(transcript);
        var kept = LongNumberDigits(rewritten);
        var needed = retracted.Count == 0 ? transcript : SpokenNumbers.RemoveDigitAmounts(transcript, retracted);
        if (!LongNumberDigits(needed).All(d => kept.GetValueOrDefault(d.Key) >= d.Value)) return "a 3+ digit run from the transcript was dropped";

        var rewrittenAmounts = SpokenNumbers.Amounts(rewritten);
        if (SpokenNumbers.WordAmounts(transcript).Where(a => !retracted.Contains(a)).FirstOrDefault(a => !rewrittenAmounts.Contains(a)) is var missing and > 0)
            return $"spoken amount {missing} is missing from the rewrite";

        var said = SpokenNumbers.Amounts(transcript);
        if (rewrittenAmounts.FirstOrDefault(a => !said.Contains(a) && !(knownNumbers?.Contains(a) ?? false)) is var invented and > 0)
            return $"amount {invented} appears in the rewrite but was not said";
        return null;
    }

    private static Dictionary<int, int> LongNumberDigits(string text) =>
        Regex.Matches(text, @"\d{3,}").SelectMany(m => m.Value).GroupBy(c => (int)char.GetNumericValue(c)).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>English description of what the bot is waiting for, given to the AI so a spoken answer is read in context.</summary>
    private static string DescribeVoiceSituation(Seller seller, SessionContextData ctx)
    {
        var state = seller.Session!.State;
        var core = state switch
        {
            ConversationState.Idle when seller.OnboardingComplete =>
                "The bot is idle: the seller can type a command (orders today, mark 3 shipped, stock Kurti 20, catalog, delivery 250, receipt), dictate a customer order, or tell the bot about new products they sell.",
            ConversationState.Idle => "The bot is at the start of setup; the seller can say start, pick a language or say \"Setup shuru karein\".",
            ConversationState.OnboardingLanguage or ConversationState.AwaitingLanguageChoice => "Waiting for the seller to choose a language: Roman Urdu, Urdu or English.",
            ConversationState.OnboardingStartChoice => "Waiting for a choice: \"Setup shuru karein\", \"Guide dekhein\" or \"Purana data\" (bring customers/products from an old system).",
            ConversationState.OnboardingBusinessName => "Waiting for the name of the seller's shop/business.",
            ConversationState.OnboardingOptionalDetails => "Waiting for the shop's city, business type and Instagram handle in ONE step, comma-separated (e.g. \"Lahore, Clothing, @ayesha\"), or \"skip\".",
            ConversationState.OnboardingCatalogSize => "Waiting for how many products the seller has: a number, \"Chhota (20 se kam)\" or \"Bara (20+)\".",
            ConversationState.OnboardingAddProduct =>
                "Waiting for products to add to the catalog, each as \"Name - price\" (e.g. \"Lawn Suit - 3500\"), or \"done\" when finished. " +
                "A seller saying what they stock (\"4 lawn suits at 3500\") is adding a product, not placing an order.",
            ConversationState.AwaitingOrderConfirmation =>
                "Waiting for yes or no on the order draft just shown; a change such as \"delivery 300\" or \"advance 500\" is also valid.",
            ConversationState.AwaitingOrderMissingFields => $"Waiting for a missing order detail ({ctx.PendingMissingField ?? "customer name or phone"}).",
            ConversationState.AwaitingCancelConfirmation or ConversationState.AwaitingBulkStatusConfirmation or ConversationState.AwaitingDuplicateOrderConfirmation
                or ConversationState.AwaitingCodCollectedConfirmation or ConversationState.AwaitingResetConfirmation or ConversationState.AwaitingDeleteCustomerConfirmation
                or ConversationState.AwaitingLoyaltyDiscountConfirmation or ConversationState.AwaitingMultiOrderConfirmation
                or ConversationState.AwaitingSupportReplyConfirmation or ConversationState.AwaitingImportConfirmation => "Waiting for a yes or no confirmation.",
            ConversationState.AwaitingVoiceConfirmation => "Waiting for yes or no on the actions the bot just listed from the seller's previous voice note.",
            ConversationState.AwaitingClarificationChoice or ConversationState.AwaitingOrderGroupingChoice or ConversationState.AwaitingRuntimeFilterChoice
                or ConversationState.AwaitingBroadcastAudienceChoice or ConversationState.AwaitingBroadcastChannelChoice or ConversationState.AwaitingReceiptOrderChoice
                or ConversationState.AwaitingSupportQueryPick => "Waiting for a numbered choice.",
            _ => $"The bot is in the step \"{state}\" and expects a short typed answer."
        };

        if (ctx.ClarificationOptions is { Count: > 0 } options)
            core += " Options offered: " + string.Join(" | ", options.Select((o, i) => $"{i + 1}) {o}")) + ".";
        if (ctx.PendingPriceProducts is { Count: > 0 } waiting)
            core += $" The bot is waiting for the price of these new products: {string.Join(", ", waiting)}. A spoken price (one price for all, or one each) is for them: " +
                    "write it as one sentence naming that price and who it is for (\"sab ki price 5000\" or \"chadar 800, dupatte 1200\"), never as a new product.";
        if (ctx.PendingNewProductName is { } unknown)
            core += $" The bot asked about the product \"{unknown}\" which is not in the catalog: 1 = add it as a new product, 2 = it is another name for an existing catalog product (then the seller names which one).";
        return core;
    }

    public async Task HandleImageMessageAsync(string fromPhoneNumber, string mediaId, string? caption, CancellationToken ct = default)
    {
        _db.MessageLogs.Add(new MessageLog { Phone = fromPhoneNumber, Direction = "inbound", RawText = $"[image {mediaId}] {caption}" });
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);

        if (!seller.OnboardingComplete)
        {
            await ReplyAsync(seller, "Pehle setup complete kar lein — phir screenshot bhej saktay hain.", ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        if (await TryHandleBillingAsync(seller, session, ctx, "", ct))
        {
            await PersistAsync(session, ctx, ct);
            return;
        }

        // Receipt logo/banner pictures are not paid AI calls, so only order/receipt screenshots count towards the hourly cap.
        if (_mediaLimiter is { } imageLimiter && !CommandParser.TryParseBrandingCaption(caption, out _)
            && !imageLimiter.TryConsume(fromPhoneNumber, MediaKind.Image, out var imageWait))
        {
            await ReplyAsync(seller, MediaLimitText(MediaKind.Image, imageLimiter.LimitFor(MediaKind.Image), imageWait), ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        var media = _media is null ? null : await _media.DownloadAsync(mediaId, ct);
        if (media is null)
        {
            await HandleUnsupportedMediaAsync(fromPhoneNumber, "image", ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        // A picture captioned "logo" / "banner" is receipt branding, not an order screenshot.
        if (CommandParser.TryParseBrandingCaption(caption, out var brandingKind))
        {
            await SaveBrandingAsync(seller, brandingKind, media.Value.Bytes, ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        // A screenshot starts fresh: any half-finished draft/prompt is dropped.
        ResetFlowContext(ctx);
        SetState(session, ConversationState.Idle);

        var catalog = await LoadCatalogAsync(seller, ct);
        var analysis = await _ai.AnalyzeImageAsync(AiContext(seller, catalog), new AiImageInput(media.Value.Bytes, media.Value.MimeType, caption), ct);

        if (analysis.Receipt is { Amount: > 0 } receipt)
            await StartReceiptMatchAsync(seller, session, ctx, receipt, ct);
        else
            await HandleAnalysisAsync(seller, session, ctx, analysis, catalog, fromScreenshot: true, ct);

        await PersistAsync(session, ctx, ct);
    }

    private async Task StartReceiptMatchAsync(Seller seller, ConversationSession session, SessionContextData ctx, AiPaymentReceipt receipt, CancellationToken ct)
    {
        var unpaid = await _db.Orders.Include(o => o.Customer)
            .Where(o => o.SellerId == seller.Id && o.PaymentStatus == PaymentStatus.Unpaid && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
        var exact = unpaid.Where(o => o.Total == receipt.Amount).ToList();
        var candidates = (exact.Count > 0 ? exact : unpaid).Take(3).ToList();

        var header = $"💰 Payment receipt mila — {Formatters.Money(receipt.Amount!.Value)}" +
                     (receipt.Provider is null ? "" : $" ({receipt.Provider}{(receipt.TransactionId is null ? "" : $" TID: {receipt.TransactionId}")})");
        if (candidates.Count == 0)
        {
            await ReplyAsync(seller, $"{header}\n\nLekin koi unpaid order nahi mila jis se match ho.", ct);
            return;
        }

        ctx.ReceiptCandidateOrderIds = candidates.Select(o => o.Id).ToList();
        ctx.ReceiptAmount = receipt.Amount;
        ctx.ReceiptProvider = receipt.Provider;
        ctx.ReceiptTransactionId = receipt.TransactionId;
        SetState(session, ConversationState.AwaitingReceiptOrderChoice);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, $"{header}\n\nKonsa order match karta hai?",
            candidates.Select(o => $"Order #{o.Id} - {o.Customer?.Name}").ToList(), ct);
    }

    private async Task HandleReceiptOrderChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var ids = ctx.ReceiptCandidateOrderIds ?? new List<int>();
        var m = OrderRef.Match(message);
        int? orderId = null;
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            orderId = ids.Contains(n) ? n : n >= 1 && n <= ids.Count ? ids[n - 1] : null;

        if (orderId is null)
        {
            if (CommandParser.TryParse(message) is { } command || CancelWords.Contains(message.Trim()))
            {
                ClearReceipt(ctx);
                SetState(session, ConversationState.Idle);
                if (CommandParser.TryParse(message) is { } cmd) await ExecuteCommandAsync(seller, session, ctx, cmd, ct);
                else await ReplyAsync(seller, "Theek hai, kuch mark nahi kiya.", ct);
                return;
            }
            await ReplyAsync(seller, "Order chunein (button dabayein ya order number likhein), ya \"cancel\".", ct);
            return;
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.SellerId == seller.Id, ct);
        SetState(session, ConversationState.Idle);
        if (order is null) { ClearReceipt(ctx); return; }

        MarkFullyPaid(seller, order);
        if (order.PaymentMethod != OrderPaymentMethod.Gateway) order.PaymentMethod = OrderPaymentMethod.Manual;
        if (ctx.ReceiptTransactionId is { } tid)
            order.Notes = string.IsNullOrWhiteSpace(order.Notes) ? $"TID: {tid}" : $"{order.Notes} | TID: {tid}";

        await ReplyAsync(seller,
            $"✅ Order #{order.Id} marked PAID ({Formatters.Money(ctx.ReceiptAmount ?? order.Total)}{(ctx.ReceiptProvider is null ? "" : $", {ctx.ReceiptProvider}")}).", ct);
        ClearReceipt(ctx);
    }

    private static void ClearReceipt(SessionContextData ctx)
    {
        ctx.ReceiptCandidateOrderIds = null;
        ctx.ReceiptAmount = null;
        ctx.ReceiptProvider = null;
        ctx.ReceiptTransactionId = null;
    }

    private static void ResetFlowContext(SessionContextData ctx)
    {
        ctx.PendingOrder = null;
        ctx.PendingMissingField = null;
        ctx.PendingNewProductName = null;
        ctx.QueuedSeparateOrderItems = null;
        ctx.QueuedOrderTemplate = null;
        ctx.QueuedOrders = null;
        ctx.MultiOrders = null;
        ctx.ClarificationOptions = null;
        ClearReceipt(ctx);
    }
}
