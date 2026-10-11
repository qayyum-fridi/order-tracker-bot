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

        var decision = await InterpretVoiceAsync(seller, text, ct);
        var heard = $"🎤 Maine suna: \"{text}\"";
        if (decision.Kind == DecisionKind.Clarify)
        {
            // Clear intent but a detail is missing (which order, the new price): ask for it instead of guessing or failing.
            // When the answers are a few known choices they come as tap buttons, so the seller need not speak again.
            var options = decision.Options ?? Array.Empty<string>();
            await SendChoicesAsync(fromPhoneNumber, $"{heard}\n\n❓ {decision.Message}", options.Select(o => new ChoiceOption(o, o)).ToList(), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (decision.Kind == DecisionKind.Reject)
        {
            // Unsafe to run and not safe to offer as choices: say nothing changed, and wait for the seller to type or speak it again.
            await _sender.SendTextMessageAsync(fromPhoneNumber, $"{heard}\n\n{decision.Message}", ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        IReadOnlyList<string> steps = decision.Steps;

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
            ctx.PendingVoiceParkedAt = DateTime.UtcNow;
            ctx.PendingVoiceLogId = await NewestActionLogIdAsync(seller.Id, ct);
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
    private async Task<TurnDecision> InterpretVoiceAsync(Seller seller, string transcript, CancellationToken ct)
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

        if (result?.Question is { } question && result.Steps.Count == 0) return TurnDecision.Clarify(question, result.Options);
        if (result is { Steps.Count: > 0 })
        {
            // The model may only pick actions that make sense right now; anything else it made up is dropped (and the transcript is used if nothing is left).
            var steps = result.Actions.Count == result.Steps.Count
                ? ValidateVoiceSteps(seller.Session!.State, seller, result.Steps, result.Actions)
                : result.Steps.Select(s => s.Trim()).ToList();
            if (steps.Count > 0)
            {
                // A rewrite that changes what was said is not run, and neither is the raw transcript: the seller is asked again.
                return DecideRewrite(transcript, steps, KnownVoiceNumbers(recent, catalog));
            }
        }
        // The AI was unavailable: the transcript goes to the deterministic engine, as before.
        if (result is null) return TurnDecision.Execute(new[] { transcript });
        // The AI answered without a usable step: the transcript runs only when it reads as a plain command or answer.
        if (CommandParser.TryParse(transcript) is not null || CommandParser.IsAffirmative(transcript) || CommandParser.IsNegative(transcript))
            return TurnDecision.Execute(new[] { transcript });
        return TurnDecision.Clarify(ParserFailureQuestion);
    }

    /// <summary>
    /// The safety gate for a rewritten voice turn. Faithful: run it. Unfaithful and it would change money or an order: refuse (no tap options that
    /// could be read as approval). Unfaithful but harmless: ask again.
    /// </summary>
    public static TurnDecision DecideRewrite(string transcript, IReadOnlyList<string> steps, IReadOnlySet<long>? knownNumbers)
    {
        if (WhyUnfaithful(transcript, string.Join("\n", steps), knownNumbers) is null) return TurnDecision.Execute(steps);
        return StepsAreRisky(steps) ? TurnDecision.Reject(RejectedUnfaithful) : TurnDecision.Clarify(UnfaithfulQuestion);
    }

    // Money or order-state words. A step that is not a parsed command (e.g. "price 5000") still counts as risky when it names one of these.
    private static readonly Regex RiskyStepWord = new(
        @"\b(price|rate|delivery|discount|cancel|edit|paid|advance|payment|cod|shipped|delivered|returned|status|customer|remove|add)\b",
        RegexOptions.IgnoreCase);

    private static bool StepsAreRisky(IEnumerable<string> steps) =>
        steps.Any(s => RiskyStepWord.IsMatch(s) || CommandParser.TryParse(s) is { } command && RiskyVoiceCommands.Contains(command.Kind));

    private const string RejectedUnfaithful =
        "Yeh poori tarah samajh nahi aaya, is liye kuch nahi badla. Ek baar likh kar bhejein, jo chahiye wo saaf alfaz mein.";

    // Three different outcomes, three different replies: a rewrite that changed the meaning, a transcript nothing could read, and (above) a rewrite
    // that was used. None of them runs a step.
    private const string UnfaithfulQuestion =
        "Yeh poori tarah samajh nahi aaya — rakam, quantity ya \"nahi\" wali baat alag alag likh kar dobara bhejein.";
    private const string ParserFailureQuestion =
        "Samajh nahi aaya. Dobara bolein, ya likh kar bhejein (jaise \"Kurti - 1800\" ya \"order confirm\").";

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
        // Amounts the seller took back ("410 nahi, 420") or refused ("paanch hazaar mat karna") need not survive the rewrite, and must not be set.
        var retracted = SpokenNumbers.RetractedAmounts(transcript);
        var negated = SpokenNumbers.NegatedAmounts(transcript);
        var dropped = new HashSet<long>(retracted.Concat(negated));
        var needed = dropped.Count == 0 ? transcript : SpokenNumbers.RemoveDigitAmounts(transcript, dropped);
        var kept = DigitRuns(rewritten);
        if (!DigitRuns(needed).All(kept.Contains)) return "a 3+ digit run from the transcript was dropped or changed";

        var rewrittenAmounts = SpokenNumbers.Amounts(rewritten);
        if (SpokenNumbers.WordAmounts(transcript).Where(a => !dropped.Contains(a)).FirstOrDefault(a => !rewrittenAmounts.Contains(a)) is var missing and > 0)
            return $"spoken amount {missing} is missing from the rewrite";
        if (rewrittenAmounts.FirstOrDefault(negated.Contains) is var refused and > 0)
            return $"amount {refused} was refused by the seller";

        var said = SpokenNumbers.Amounts(transcript);
        if (rewrittenAmounts.FirstOrDefault(a => !said.Contains(a) && !(knownNumbers?.Contains(a) ?? false)) is var invented and > 0)
            return $"amount {invented} appears in the rewrite but was not said";

        // Counts below 100 ("teen piece" -> "quantity 5"). A count the seller took back ("teen nahi, do") may not come back.
        var counts = SpokenNumbers.SmallCounts(transcript);
        var refusedCounts = SpokenNumbers.RetractedCounts(transcript).Concat(SpokenNumbers.NegatedCounts(transcript)).ToHashSet();
        foreach (var count in CountsInRewrite(rewritten))
        {
            if (refusedCounts.Contains(count)) return $"count {count} was refused by the seller";
            if (!counts.Contains(count) && !(knownNumbers?.Contains(count) ?? false)) return $"count {count} appears in the rewrite but was not said";
        }

        // "Kurti nahi, Lawn chahiye": an item the seller refused may not come back in the rewrite without a negation.
        var rewriteWords = WordsOf(rewritten).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!rewriteWords.Any(RefusalWords.Contains) && NegatedItemWords(transcript).FirstOrDefault(rewriteWords.Contains) is { } refusedItem)
            return $"'{refusedItem}' was refused in the transcript but is in the rewrite";

        // "se kam", "se zyada", "3500 tak": the comparator is part of the price.
        if (Comparator.IsMatch(transcript) && !RewriteComparator.IsMatch(rewritten)) return "a comparator (se kam / se zyada / tak) was dropped";
        // "aur delivery", "delivery alag", "including delivery": the rewrite must still say what happens to delivery.
        if (DeliveryWord.IsMatch(transcript) && !DeliveryWord.IsMatch(rewritten)) return "the delivery part of the transcript was dropped";
        if (InclusionWord.IsMatch(transcript) && !InclusionWord.IsMatch(rewritten)) return "'including' / 'included' was dropped";

        // "Haan lekin quantity do kar do", "haan, theek hai... nahi ruko": a bare yes keeps the approval and drops the condition, the hesitation or the refusal.
        if (BareAffirmation.IsMatch(rewritten.Trim()) && ReversalWord.IsMatch(transcript))
            return "a bare yes drops a condition, hesitation or refusal the seller added";
        return null;
    }

    private static readonly Regex BareAffirmation = new(@"^(yes|y|ha|haan|han|hanji|ji|jee|ok|okay|theek hai|thik hai)[.!]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ReversalWord = new(@"(?:\b(?:lekin|magar|par|but|however|nahi|nahin|nahee|mat|na|no|ruk|ruko|rukna|pehle|phir|cancel|agar|shayad|soch|sochta|sochna|sochun|dekhta|dekhti|dekhungi|hmm|hmmm|baad|abhi|wait|if|maybe)\b)|\?|\.\.\.|…", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Comparator = new(@"\bse\s+(?:kam|kum|zyada|zyaada|ziada|upar|neeche|less|more)\b|\bton\s+(?:ghatt|ghat|kam|chhota|zyada|vadh|vadha|upar)\b|\b(?:less|more)\s+than\b|\b(?:under|below|above|over|upto|up\s+to)\b|(?:\d|\b(?:hazaar|hazar|hajar|sau|lakh|k)\b)\s+tak\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RewriteComparator = new(@"\b(?:under|below|above|over|less|more|upto|up\s+to|max|maximum|min|minimum|kam|zyada|tak|ghatt|vadh|vadha)\b|<|>|≤|≥", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DeliveryWord = new(@"\b(?:delivery|shipping|courier|postage)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex InclusionWord = new(@"\b(?:including|included|inclusive|incl|shamil)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A count or index in a rewrite: 1-99 and not an id or index ("1 = 3", "remove 2", "order 12", "#7" are checked against ids and the amount rules instead).
    private static readonly Regex RewriteCount = new(@"(?<![\w#.])(?<!\b(?:remove|item|order|receipt|status|id|number|tracking)\s)(\d{1,2})(?![\w.])(?!\s*=)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static IEnumerable<int> CountsInRewrite(string rewritten) =>
        RewriteCount.Matches(rewritten).Select(m => int.Parse(m.Groups[1].Value)).Where(n => n > 0);

    private static readonly HashSet<string> RefusalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "mat", "nahi", "nahin", "nahee", "nai", "na", "no", "never", "نہیں", "مت"
    };

    // Words that are never the item a seller refused ("Kurti nahi" refuses the kurti, not "bhai" or "do").
    private static readonly HashSet<string> NonItemWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "bhai", "ji", "haan", "han", "ha", "yar", "ok", "lekin", "aur", "ka", "ki", "ke", "ko", "mein", "me", "se", "to", "hi", "ye", "yeh", "wo", "woh",
        "wala", "wali", "wale", "chahiye", "karo", "karna", "kar", "do", "dena", "likho", "likhna", "bolna", "bhejo", "bhej", "hai", "hain", "ho",
        "sirf", "abhi", "ab", "sab", "bas", "price", "rate", "ruk", "ruko", "rukna", "jao", "theek", "thik", "pehle", "phir", "ya", "sau", "hazar",
        "hazaar", "hajar", "lakh", "k", "tak", "kal", "aaj", "order", "orders", "id", "number", "nambar", "numbar", "tracking", "receipt"
    };

    private static string[] WordsOf(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToArray();

    /// <summary>Words the seller refused: the one or two words right before a negation, minus numbers and filler ("Kurti nahi" -> kurti).
    /// A word the seller says again after the negation ("cancel mat karna... cancel kar do") was not refused.</summary>
    private static IEnumerable<string> NegatedItemWords(string transcript)
    {
        var words = WordsOf(transcript);
        for (var i = 0; i < words.Length; i++)
        {
            if (!RefusalWords.Contains(words[i])) continue;
            for (var j = Math.Max(0, i - 2); j < i; j++)
            {
                var word = words[j];
                if (RefusalWords.Contains(word) || NonItemWords.Contains(word) || SpokenNumbers.IsNumberWord(word) || word.All(char.IsDigit)) continue;
                if (words.Skip(i + 1).Contains(word, StringComparer.OrdinalIgnoreCase)) continue;
                yield return word;
            }
        }
    }

    // Runs of 3+ digits as whole numbers: "3,500" and "3500" match, while "5300" does not match "3500" (a digit count would accept both).
    private static HashSet<string> DigitRuns(string text) =>
        Regex.Matches(Regex.Replace(text, @"(?<=\d)[,-](?=\d)", ""), @"\d{3,}").Select(m => m.Value).ToHashSet();


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
