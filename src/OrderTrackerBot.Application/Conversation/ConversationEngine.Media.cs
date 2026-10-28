using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Screens 5d-2..5d-4: a forwarded screenshot is either an order (Instagram/TikTok DM) or a payment receipt.
public partial class ConversationEngine
{
    private static readonly Regex OrderRef = new(@"#?(\d+)", RegexOptions.Compiled);

    // A sheet/file mentioned together with variants (size, colour, verient) in one message, in Roman Urdu / Urdu script / English.
    internal static readonly Regex VariantsInFile = new(
        @"(?:excel|ایکسل|sheet|شیٹ|file|فائل).*(?:variant|verient|ویر[اائیی]+ن[ٹت]|size|سائز|colou?r|rang|رنگ)|(?:variant|verient|ویر[اائیی]+ن[ٹت]|size|سائز|colou?r|rang|رنگ).*(?:excel|ایکسل|sheet|شیٹ|file|فائل)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Voice note: transcribe, echo what was heard so the seller can catch mistakes, then handle it exactly like typed text.</summary>
    public async Task HandleAudioMessageAsync(string fromPhoneNumber, string mediaId, string? messageId = null, string? replyToId = null, CancellationToken ct = default, DateTime? sentAtUtc = null)
    {
        _sentAtUtc = sentAtUtc;
        var handling = System.Diagnostics.Stopwatch.StartNew();
        // Meta's send time to the start of handling: WhatsApp delivery, the wait in the seller's queue, and any earlier message.
        if (sentAtUtc is { } sentUtc)
            _logger?.LogInformation("Voice note: {LagMs} ms from WhatsApp's send time to the start of handling", (long)(DateTime.UtcNow - sentUtc).TotalMilliseconds);
        var startLogId = await NewestActionLogIdAsync((await LoadOrCreateSellerAsync(fromPhoneNumber, ct)).Id, ct);
        _lastAudioHash = null;
        await HandleAudioCoreAsync(fromPhoneNumber, mediaId, messageId, replyToId, ct);
        _logger?.LogInformation("Voice note: handled in {TotalMs} ms, reply included", handling.ElapsedMilliseconds);

        // The actions this note made are remembered for "undo note". A note that made none (a refused reply) keeps the earlier range.
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var endLogId = await NewestActionLogIdAsync(seller.Id, ct);
        if (endLogId <= startLogId) return;
        var ctx = SessionContextData.FromJson(seller.Session!.ContextJson);
        ctx.LastVoiceNoteStartLogId = startLogId;
        ctx.LastVoiceNoteEndLogId = endLogId;
        ctx.LastVoiceNoteHash = _lastAudioHash;
        if (_lastAudioHash is { } hash)
        {
            var hashes = ctx.ProcessedVoiceHashes ?? new List<string>();
            hashes.Remove(hash);
            hashes.Add(hash);
            ctx.ProcessedVoiceHashes = hashes.TakeLast(20).ToList();
        }
        await PersistAsync(seller.Session!, ctx, ct);
    }

    /// <summary>SHA-256 of the audio being handled now (set by HandleAudioCoreAsync), recorded only when the note made actions.</summary>
    private string? _lastAudioHash;

    private async Task HandleAudioCoreAsync(string fromPhoneNumber, string mediaId, string? messageId, string? replyToId, CancellationToken ct)
    {
        if (_transcriber is not { IsConfigured: true })
        {
            await HandleUnsupportedMediaAsync(fromPhoneNumber, "audio", ct);
            return;
        }

        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        // A voice note sent as a reply to an earlier voice note is usually a correction of it: the earlier note's orders already exist,
        // so nothing runs until the seller undoes them. This is said, not guessed.
        if (replyToId is not null && SessionContextData.FromJson(seller.Session!.ContextJson).ProcessedVoiceNoteIds?.Contains(replyToId) == true)
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get("voice-confirm.009"), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (!TryTakeUsage(seller, fromPhoneNumber, MediaKind.Voice, out var voiceDecision, out var voiceWait))
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber, UsageLimitText(seller, MediaKind.Voice, voiceDecision, voiceWait), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (_features.VoiceAck) await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get("media.ack"), ct);
        var voiceClock = System.Diagnostics.Stopwatch.StartNew();
        var media = _media is null ? null : await _media.DownloadAsync(mediaId, ct);
        var downloadMs = voiceClock.ElapsedMilliseconds;
        _lastAudioHash = media is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media.Value.Bytes));
        if (_lastAudioHash is not null && SessionContextData.FromJson(seller.Session!.ContextJson).ProcessedVoiceHashes?.Contains(_lastAudioHash) == true)
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get("voice-undo.003"), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        var vocabularyStartMs = voiceClock.ElapsedMilliseconds;
        var vocabulary = media is null ? null : await BuildVoiceVocabularyAsync(seller, ct);
        var vocabularyMs = voiceClock.ElapsedMilliseconds - vocabularyStartMs;
        var text = media is null ? null : await _transcriber.TranscribeAsync(media.Value.Bytes, media.Value.MimeType, vocabulary, ct);
        var transcribedMs = voiceClock.ElapsedMilliseconds;
        if (text is null)
        {
            if (_issues is not null)
                await _issues.ReportAsync(Abstractions.IssueCodes.VoiceTranscriptionFailed, fromPhoneNumber,
                    media is null ? "media download failed" : "transcription returned nothing", null, ct);
            await _sender.SendTextMessageAsync(fromPhoneNumber,
                MessageTemplates.Get("media.001"), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (messageId is not null) await RememberVoiceNoteAsync(seller, messageId, ct);
        var heard = $"🎤 Maine suna: \"{text}\"";
        // Variants (sizes / colours) that the seller says are in a sheet: the voice rewrite keeps only the product, so the variants
        // would be lost. Nothing is saved; the seller sends the file and the import handles the rows.
        if (VariantsInFile.IsMatch(text) && SessionContextData.FromJson(seller.Session!.ContextJson).PendingProductJson is null)
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber, $"{heard}\n\n{MessageTemplates.Get("commands.053")}", ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // A transcript that is only a phone number needs no rewrite (a second, slow model call): it is the answer as typed.
        var joined = JoinSpacedPhones(text.Trim().Trim('.', '!', '?', '،', '۔', ' '));
        _droppedVoiceSteps.Clear();
        var decision = joined.All(c => char.IsDigit(c) || c == '+') && IsPlausiblePhone(joined)
            ? TurnDecision.Execute(new[] { joined })
            : await InterpretVoiceAsync(seller, text, ct);
        if (decision.Kind != DecisionKind.Execute) LogNotUnderstood("voice-" + decision.Kind.ToString().ToLowerInvariant(), seller.Session!.State, text);
        // Steps the AI made up or that do not match the words spoken are not run; the seller is told which ones, so nothing vanishes silently.
        // Shown on every reply path (clarify, refuse and run), not only the run path.
        foreach (var (step, template) in _droppedVoiceSteps)
            heard += $"\n{MessageTemplates.Get(template, step)}";
        if (decision.Kind == DecisionKind.Clarify)
        {
            // Clear intent but a detail is missing (which order, the new price): ask for it instead of guessing or failing.
            // When the answers are a few known choices they come as tap buttons, so the seller need not speak again.
            var options = decision.Options ?? Array.Empty<string>();
            var choices = options.Any() ? options.Select(o => new ChoiceOption(o, o)).ToList() : VoiceFallbackChoices.ToList();
            await SendChoicesAsync(fromPhoneNumber, $"{heard}\n\n❓ {decision.Message}", choices, ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (decision.Kind == DecisionKind.Reject)
        {
            // Unsafe to run and not safe to offer as choices: say nothing changed. The taps are only safe reads (orders, menu), never an approval.
            await SendChoicesAsync(fromPhoneNumber, $"{heard}\n\n{decision.Message}", VoiceFallbackChoices, ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        _logger?.LogInformation("Voice note: download {DownloadMs} ms, vocabulary {VocabularyMs} ms, up to transcript {TranscribedMs} ms, understood {TotalMs} ms (audio {Bytes} bytes, transcript {Words} words, decision {Kind})",
            downloadMs, vocabularyMs, transcribedMs, voiceClock.ElapsedMilliseconds, media?.Bytes.Length ?? 0, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, decision.Kind);
        IReadOnlyList<string> steps = decision.Steps;

        // City / business type / handle are one answer ("Lahore, Clothing, @x"); separate steps would leave the first one to
        // complete the question and push the rest into the next onboarding step.
        if (steps.Count > 1 && seller.Session!.State == ConversationState.OnboardingOptionalDetails)
            steps = new[] { string.Join(", ", steps) };

        // "done"/"skip" end setup and start the trial: the rewrite must not add them unless the seller said they were finished.
        // A step that ends with one of them is checked too, not only a step that is just the word.
        if (!seller.OnboardingComplete && !FinishWords.IsMatch(text))
        {
            var withoutTrailing = steps.Select(s => TrailingFinishWord.Replace(s, "").Trim()).ToList();
            if (withoutTrailing.All(s => s.Length > 0)) steps = withoutTrailing;
        }
        if (!seller.OnboardingComplete && !FinishWords.IsMatch(text) && steps.Any(s => DoneOrSkip.IsMatch(s.Trim())))
        {
            var withoutFinish = steps.Where(s => !DoneOrSkip.IsMatch(s.Trim())).ToList();
            steps = withoutFinish.Count > 0 ? withoutFinish : new List<string> { text };
        }

        if (steps.Count != 1 || !string.Equals(steps[0], text, StringComparison.OrdinalIgnoreCase))
            heard += $"\n➡️ Samjha: {string.Join("  →  ", steps.Select(s => $"\"{s}\""))}";

        // Money-changing or hard-to-reverse actions (price edit, cancel, status change...) wait for a YES before anything runs.
        var (safe, risky) = SplitVoiceSteps(seller, steps);
        if (risky.Count == 0)
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber, heard, ct);
            await RunVoiceStepsAsync(fromPhoneNumber, steps, _sentAtUtc, ct);
            return;
        }
        if (safe.Count == 0)
        {
            await ParkVoiceStepsAsync(fromPhoneNumber, seller, risky, heard, ct);
            return;
        }

        // Mixed note: the safe steps run first. The risky ones wait for a YES only when the session is idle again; an open order draft keeps its state.
        await RunVoiceStepsAsync(fromPhoneNumber, safe, _sentAtUtc, ct);
        var after = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        if (after.Session!.State == ConversationState.Idle)
        {
            await ParkVoiceStepsAsync(fromPhoneNumber, after, risky, heard, ct);
            return;
        }
        await _sender.SendTextMessageAsync(fromPhoneNumber, $"{heard}\n\n{MessageTemplates.Get("voice-confirm.005")}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Safe read-only taps offered when a voice note is refused or not understood, so the seller has a next step without retyping.</summary>
    private static readonly ChoiceOption[] VoiceFallbackChoices =
    {
        new("orders today", "orders today"),
        new("menu", "menu"),
    };

    /// <summary>Keeps the id of the last 20 voice notes handled, so a later reply to one of them is recognised.</summary>
    private async Task RememberVoiceNoteAsync(Seller seller, string messageId, CancellationToken ct)
    {
        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);
        var ids = ctx.ProcessedVoiceNoteIds ?? new List<string>();
        ids.Remove(messageId);
        ids.Add(messageId);
        ctx.ProcessedVoiceNoteIds = ids.TakeLast(20).ToList();
        await PersistAsync(session, ctx, ct);
    }

    /// <summary>A "skip" or "done" the rewrite tacked on the end of a step ("Sargodha, Bakery, skip"); the seller did not say it.</summary>
    private static readonly Regex TrailingFinishWord = new(@"[,\s]+(?:done|skip)\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Words that really mean "I am finished / skip this" in the transcript (Roman Urdu, English or Urdu script).</summary>
    private static readonly Regex FinishWords = new(
        @"\b(?:done|skip|finish(?:ed)?|complete|khatam|khtm|bas|hogaya|ho\s+gaya|mukammal|chor|chhor|baad\s+mein)\b|ڈن|ختم|بس|ہو\s*گیا|مکمل|سکپ|چھوڑ|بعد\s*میں",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The seller's own words (shop, customers, products) given to the speech model so names are heard correctly.</summary>
    private async Task<List<string>> BuildVoiceVocabularyAsync(Seller seller, CancellationToken ct)
    {
        // Bot vocabulary first (the keyword list is capped): "catalog mein add kar dein" was heard as "address mein".
        var names = new List<string> { "catalog", "product", "products", "order", "delivery" };
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
    // States where the bot shows a numbered list; a spoken pick there is read as its number, with no AI call to mishear it.
    private static readonly HashSet<ConversationState> NumberedChoiceStates = new()
    {
        ConversationState.OnboardingIndustry, ConversationState.AwaitingClarificationChoice, ConversationState.AwaitingOrderGroupingChoice,
        ConversationState.AwaitingRuntimeFilterChoice, ConversationState.AwaitingBroadcastAudienceChoice, ConversationState.AwaitingBroadcastChannelChoice,
        ConversationState.AwaitingReceiptOrderChoice, ConversationState.AwaitingSupportQueryPick,
    };

    private static readonly HashSet<CommandKind> ReadOnlyVoiceCommands = new()
    {
        CommandKind.Help, CommandKind.Menu, CommandKind.OrdersToday, CommandKind.PendingOrders, CommandKind.TodaysSummary, CommandKind.Catalog, CommandKind.UnpaidOrders,
    };

    private static readonly HashSet<string> SmallTalkWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "hi", "hey", "salam", "assalam", "alaikum", "bhai", "bhaiya", "kya", "hogaya", "hua", "kaise", "kaisay", "kaisi", "ho",
        "aap", "tum", "sir", "bolo", "sun", "suno", "سلام", "علیکم", "بھائی", "کیا", "ہوگیا", "ہوا", "ہو", "آپ", "کیسے", "کیسی"
    };

    /// <summary>True when the transcript is only greeting or small-talk words: no digits, and nothing but words like hello, bhai, kya hogaya.</summary>
    internal static bool IsSmallTalkOnly(string transcript)
    {
        if (transcript.Any(char.IsDigit)) return false;
        return Regex.Split(transcript, @"[^\p{L}]+").Where(w => w.Length > 0).All(SmallTalkWords.Contains);
    }

    private async Task<TurnDecision> InterpretVoiceAsync(Seller seller, string transcript, CancellationToken ct)
    {
        if (NumberedChoiceStates.Contains(seller.Session!.State) && SpokenNumbers.ChoiceNumber(transcript) is { } pick)
            return TurnDecision.Execute(new[] { pick.ToString() });
        // A greeting or small talk is not an order or a command: say so plainly, and do not let the model invent steps from it.
        if (IsSmallTalkOnly(transcript))
            return TurnDecision.Clarify(MessageTemplates.Get("voice.smalltalk"));
        // A plain read-only command ("orders today", "menu") needs no rewrite: it runs as spoken, the same as a typed one.
        if (CommandParser.TryParse(transcript) is { } readOnly && ReadOnlyVoiceCommands.Contains(readOnly.Kind))
            return TurnDecision.Execute(new[] { transcript });
        // "setup mein abhi kya baqi hai?", "ab kya karna hai": a request for the next step is answered with the tip for the current step, as typed.
        if (CommandParser.IsGuidanceRequest(transcript))
            return TurnDecision.Execute(new[] { transcript });
        var ctx = SessionContextData.FromJson(seller.Session!.ContextJson);
        var catalog = await LoadCatalogAsync(seller, ct);
        var recent = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id).OrderByDescending(o => o.Id).Take(5).ToListAsync(ct);
        // Only the 40 customers with the latest orders are sent to the model: pick them in SQL instead of loading every customer and order.
        var knownCustomers = (await _db.Customers.Include(c => c.Orders)
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null)
            .OrderByDescending(c => c.Orders.Max(o => (int?)o.Id)).ThenByDescending(c => c.Id).Take(40).ToListAsync(ct))
            .Select(DescribeCustomerForVoice).ToList();
        var lastMessages = await _db.MessageLogs.Where(m => m.Phone == seller.WhatsAppPhoneNumber)
            .OrderByDescending(m => m.Id).Take(6).ToListAsync(ct);
        var exchanges = lastMessages.AsEnumerable().Reverse()
            .Select(m => $"{(m.Direction == "outbound" ? "Bot" : "Seller")}: {(m.RawText.Length > 300 ? m.RawText[..300] : m.RawText)}").ToList();

        // A spoken "cancel" while an order draft waits is a cancel, whatever the model would make of it ("kis order ko cancel karna hai?").
        if (ctx.PendingOrder is not null && OpensWithLeavePhrase(transcript.Trim().Trim('.', '!', '?', '،', '۔', ' ')))
            return TurnDecision.Execute(new[] { "cancel" });

        var rewriteClock = System.Diagnostics.Stopwatch.StartNew();
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
        _logger?.LogInformation("Voice rewrite: AI answered in {RewriteMs} ms with {Steps} step(s)", rewriteClock.ElapsedMilliseconds, result?.Steps.Count ?? 0);

        if (result?.Question is { } question && result.Steps.Count == 0) return TurnDecision.Clarify(question, result.Options);
        if (result is { Steps.Count: > 0 })
        {
            // A step must come from the words actually spoken (its span) and carry only amounts said in those words; anything else is dropped.
            var known = KnownVoiceNumbers(recent, catalog, lastMessages.Select(m => m.RawText));
            result = KeepSpokenSteps(transcript, result, known);
            // Every step was dropped and the note asks to see orders: ask today or yesterday instead of refusing the note as unclear.
            if (result.Steps.Count == 0 && _droppedVoiceSteps.Count > 0 && AsksToSeeOrders(transcript))
            {
                _droppedVoiceSteps.Clear();
                return TurnDecision.Clarify(MessageTemplates.Get("voice-confirm.012"), new[] { "orders today", "orders yesterday" });
            }
            // The model may only pick actions that make sense right now; anything else it made up is dropped (and the transcript is used if nothing is left).
            var steps = result.Actions.Count == result.Steps.Count
                ? ValidateVoiceSteps(seller.Session!.State, seller, result.Steps, result.Actions)
                : result.Steps.Select(s => s.Trim()).ToList();
            // "Yesterday's orders" is never run from a note that asks for orders: the speech model writes today's day word as "کل" often enough.
            if (steps.Any(s => s.Trim().Equals("orders yesterday", StringComparison.OrdinalIgnoreCase)) && AsksToSeeOrders(transcript))
            {
                _droppedVoiceSteps.Clear();
                return TurnDecision.Clarify(MessageTemplates.Get("voice-confirm.012"), new[] { "orders today", "orders yesterday" });
            }
            if (steps.Count > 0)
            {
                // Waiting for dates: nothing here is money or an order, and dates legitimately add digits (a day, a year) the seller spoke as words ("yakum", "do hazar chhabees").
                if (seller.Session!.State == ConversationState.AwaitingCustomDateRange) return TurnDecision.Execute(CorrectMarginQuestion(transcript, CorrectAccountReset(transcript, steps)));
                // A rewrite that changes what was said is not run, and neither is the raw transcript: the seller is asked again.
                // Reason only (never the transcript text): makes refused rewrites visible in the logs.
                if (WhyUnfaithful(transcript, string.Join("\n", steps), known) is { } why)
                    _logger?.LogWarning("Voice rewrite refused: {Reason} (risky={Risky})", why, StepsAreRisky(steps));
                return DecideRewrite(transcript, CorrectMarginQuestion(transcript, CorrectAccountReset(transcript, steps)), known);
            }
        }
        // The AI was unavailable: the transcript goes to the deterministic engine, as before, unless it has an amount word the engine cannot read.
        if (result is null) return UnknownAmountDecision(transcript, Array.Empty<string>()) ?? TurnDecision.Execute(new[] { transcript });
        // The AI answered without a usable step: the transcript runs only when it reads as a plain command or answer.
        if (CommandParser.TryParse(transcript) is not null || CommandParser.IsAffirmative(transcript) || CommandParser.IsNegative(transcript))
            return TurnDecision.Execute(new[] { transcript });
        return TurnDecision.Clarify(MessageTemplates.Get("voice.parse", Industry(seller).SimpleProduct));
    }

    /// <summary>Steps removed by <see cref="KeepSpokenSteps"/> for the current voice note; shown to the seller with the echo.</summary>
    private readonly List<(string Step, string Template)> _droppedVoiceSteps = new();

    /// <summary>
    /// Keeps the steps whose span is really in the transcript and whose amounts were said inside that span (or are known to the seller's data).
    /// Steps without a span keep the whole-note check. A dropped step is logged with its reason only, never with the transcript text.
    /// </summary>
    private AiVoiceInterpretation KeepSpokenSteps(string transcript, AiVoiceInterpretation result, IReadOnlySet<long> known)
    {
        var keepActions = result.Actions.Count == result.Steps.Count;
        var steps = new List<string>();
        var actions = new List<string>();
        var spans = new List<string>();
        var seen = new HashSet<(string Text, string Span)>();
        for (var i = 0; i < result.Steps.Count; i++)
        {
            var span = i < result.Spans.Count ? result.Spans[i] : "";
            // The same words from the same spoken part twice (a model retry) would save the same order twice: keep the first only.
            if (span.Trim().Length > 0 && !seen.Add((result.Steps[i], span)))
            {
                _logger?.LogInformation("Voice step skipped: duplicate of an earlier step");
                continue;
            }
            // A shop-name correction ("meri dukaan ka naam hai ...") is kept with the model's spelling: the name is often written in another script than it was said in.
            var shopNameRename = CommandParser.TryParseShopName(transcript, out _) && CommandParser.TryParseShopName(result.Steps[i], out _);
            if (!shopNameRename && SpanProblem(transcript, result.Steps[i], span, known) is { } problem)
            {
                _logger?.LogWarning("Voice step dropped: {Reason}", problem.Reason);
                _droppedVoiceSteps.Add((result.Steps[i], problem.Template));
                continue;
            }
            steps.Add(result.Steps[i]);
            if (keepActions) actions.Add(result.Actions[i]);
            spans.Add(span);
        }
        return new AiVoiceInterpretation { Steps = steps, Actions = actions, Spans = spans, Question = result.Question, Options = result.Options };
    }

    /// <summary>Why a step with a span must not run; null when it is fine or has no span (the whole-note check applies then).</summary>
    private static (string Reason, string Template)? SpanProblem(string transcript, string step, string span, IReadOnlySet<long> known)
    {
        var spoken = SpeechWords(span);
        if (spoken.Trim().Length == 0)
        {
            // A bare list number with no span ("2" for a correction read as a list pick) must have been said as digits.
            if (!System.Text.RegularExpressions.Regex.IsMatch(step.Trim(), @"^\d{1,2}$")) return null;
            return System.Text.RegularExpressions.Regex.IsMatch(transcript, $@"(?<!\d){step.Trim()}(?!\d)") ? null : ("step number is not in the transcript", "voice-confirm.008");
        }
        if (!SpeechWords(transcript).Contains(spoken))
        {
            // The span is spelled differently ("pachis" for "pachees") but every amount of the step was said: the step may run.
            // Without an amount there is nothing to confirm the step against, so it is still dropped.
            var stepAmounts = SpokenNumbers.Amounts(step);
            if (stepAmounts.Count > 0)
                return stepAmounts.All(a => SpokenNumbers.Amounts(transcript).Contains(a) || known.Contains(a))
                    ? null
                    : ("span is not in the transcript", "voice-confirm.008");
            // No amount (a plain command such as "cancel order" or "status check"): its words and numbers must have been said.
            return CommandWordsSaid(transcript, step) ? null : ("span is not in the transcript", "voice-confirm.008");
        }
        var spanAmounts = SpokenNumbers.Amounts(span);
        return SpokenNumbers.Amounts(step).Any(a => !spanAmounts.Contains(a) && !known.Contains(a))
            ? ("amount is not in the step's own words", "voice-confirm.007")
            : null;
    }

    /// <summary>
    /// A step without an amount is kept when its numbers (order ids, counts) were said and most of its words were said too.
    /// A number the seller did not say ("cancel 1" when nothing was numbered) still fails.
    /// </summary>
    /// <summary>Urdu-script words for the bot's command words, so "آرڈر منسوخ" matches a step written as "cancel order".</summary>
    private static readonly (string Key, string Value)[] UrduCommandWords =
    {
        ("منسوخ", " cancel "), ("کینسل", " cancel "), ("اکاؤنٹ", " account "), ("اکاونٹ", " account "), ("ری سیٹ", " reset "), ("ری‌سیٹ", " reset "), ("میرا", " mera "), ("کر دیں", " kar dein "), ("کردیں", " kar dein "), ("کر دو", " kar do "), ("کردو", " kar do "), ("آرڈر", " order "), ("آرڈرز", " orders "), ("آرسرز", " orders "), ("آرسر", " order "), ("آج ", " today "), ("کل ", " yesterday "), ("حالت", " status "), ("سٹیٹس", " status "), ("چیک", " check "),
    };

    private static bool CommandWordsSaid(string transcript, string step)
    {
        // Any number counts here, short order ids too ("cancel 1"); DigitRuns only looks at 3+ digit amounts.
        static HashSet<string> Numbers(string text) => Regex.Matches(text, @"\d+").Select(m => m.Value).ToHashSet();
        if (!Numbers(step).All(Numbers(transcript).Contains)) return false;
        var spoken = UrduCommandWords.Aggregate(transcript, (text, pair) => text.Replace(pair.Key, pair.Value));
        var said = SpeechWords(spoken).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var words = SpeechWords(step).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && !w.All(char.IsDigit)).ToList();
        return words.Count == 0 || words.Count(said.Contains) * 2 >= words.Count;
    }

    /// <summary>
    /// True when the note asks to see orders ("orders dikhao", "آرڈرز دکھائیں") and does not act on one (cancel, mark, paid, delete, undo).
    /// The "today or yesterday" question is asked for these, since the speech model often gets the day word wrong.
    /// </summary>
    private static bool AsksToSeeOrders(string transcript)
    {
        var spoken = SpeechWords(UrduCommandWords.Aggregate(transcript, (text, pair) => text.Replace(pair.Key, pair.Value)));
        return System.Text.RegularExpressions.Regex.IsMatch(spoken, @"\borders?\b")
            && System.Text.RegularExpressions.Regex.IsMatch(spoken + transcript, @"dikh|dekh|show|batao|list|دکھ|دیکھ|بتا")
            && !System.Text.RegularExpressions.Regex.IsMatch(spoken, @"cancel|mark|ship|paid|pay|delete|hata|undo|wapas|منسوخ|کینسل|ادا");
    }

    /// <summary>Lower-case words separated by single spaces, padded on both sides, so a span can be matched as whole words.</summary>
    private static string SpeechWords(string text) =>
        " " + System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim() + " ";

    /// <summary>
    /// A voice rewrite must not turn an analysis question into the "create a discount code" how-to. When the seller talked about margin / profit and did not say anything
    /// that makes a code (a code, a percent, an expiry), a bare "create discount" step is replaced by the margin report. Plain text rules, so it holds whatever the model did.
    /// </summary>
    public static IReadOnlyList<string> CorrectMarginQuestion(string transcript, IReadOnlyList<string> steps)
    {
        // "category wise list of products" is not a sales question: the rewrite must not turn it into a sales report.
        if (steps.Any(st => st.TrimStart().StartsWith("sales by category", StringComparison.OrdinalIgnoreCase)) && !SalesWords.IsMatch(transcript))
            steps = steps.Select(st => st.TrimStart().StartsWith("sales by category", StringComparison.OrdinalIgnoreCase) ? "category products" : st).Distinct().ToList();
        if (!steps.Any(IsBareCreateDiscount) || !MarginWords.IsMatch(transcript) || MakesACode.IsMatch(transcript)) return steps;
        return steps.Select(st => IsBareCreateDiscount(st) ? "margin" : st).Distinct().ToList();
    }

    /// <summary>
    /// A voice note that asks to reset the account runs the reset (it still asks YES). A rewrite can answer "kya karun" (the help list) instead, because the words sound like a request for help.
    /// Plain text rule on the transcript, so it holds whatever the model returned.
    /// </summary>
    public static IReadOnlyList<string> CorrectAccountReset(string transcript, IReadOnlyList<string> steps)
    {
        var spoken = UrduCommandWords.Aggregate(transcript, (text, pair) => text.Replace(pair.Key, pair.Value));
        var said = string.Join(' ', spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', '۔', '!', '?', ' ');
        return CommandParser.TryParse(said) is { Kind: CommandKind.ResetAccount } ? new[] { "reset account" } : steps;
    }

    private static bool IsBareCreateDiscount(string step) =>
        System.Text.RegularExpressions.Regex.IsMatch(step.Trim(), @"^(?:create|add|new|naya)\s+discount\.?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex MarginWords = new(
        @"margin|marjin|profit|munafa|nafa|مارجن|مارگن|پرافٹ|پروفٹ|پروفیٹ|منافع|نفع",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SalesWords = new(
        @"sale|sales|bikri|bika|bikay|bikey|farokht|revenue|kamai|kamayi|income|سیل|سیلز|بکری|بکا|بکے|فروخت|کمائی|آمدنی",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex MakesACode = new(
        @"\bcode\b|coupon|expire|%|percent|فیصد|کوڈ|کوپن|\d",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The safety gate for a rewritten voice turn. Faithful: run it. Unfaithful and it would change money or an order: refuse (no tap options that
    /// could be read as approval). Unfaithful but harmless: ask again.
    /// </summary>
    public static TurnDecision DecideRewrite(string transcript, IReadOnlyList<string> steps, IReadOnlySet<long>? knownNumbers)
    {
        // An amount said with a number word we do not know ("ikhattar hazar") cannot be checked, and the rewrite may have dropped it: nothing runs.
        if (UnknownAmountDecision(transcript, steps) is { } unknownDecision) return unknownDecision;
        if (WhyUnfaithful(transcript, string.Join("\n", steps), knownNumbers) is null) return TurnDecision.Execute(steps);
        return StepsAreRisky(steps) ? TurnDecision.Reject(RejectedUnfaithful) : TurnDecision.Clarify(UnfaithfulQuestion);
    }

    /// <summary>The reply for a note with an amount word the bot cannot read; null when every amount word is known.</summary>
    private static TurnDecision? UnknownAmountDecision(string transcript, IReadOnlyList<string> steps)
    {
        if (SpokenNumbers.UnknownAmountWords(transcript) is not { Count: > 0 } unknown) return null;
        var question = MessageTemplates.Get("voice-confirm.011", unknown[0]);
        return StepsAreRisky(steps) ? TurnDecision.Reject(question) : TurnDecision.Clarify(question);
    }

    private static bool StepsAreRisky(IEnumerable<string> steps) => steps.Any(ActionRisk.IsRiskyStep);

    private const string RejectedUnfaithful =
        "Maaf kijiye, aapki baat poori tarah samajh nahi aayi, is liye koi tabdeeli nahi ki gayi. Barae meherbani apni baat saaf alfaz mein likh kar bhej dein.";

    // Three different outcomes, three different replies: a rewrite that changed the meaning, a transcript nothing could read, and (above) a rewrite
    // that was used. None of them runs a step.
    private const string UnfaithfulQuestion =
        "Maaf kijiye, yeh poori tarah samajh nahi aaya. Barae meherbani rakam, quantity ya \"nahi\" wali baat alag alag likh kar dobara bhej dein.";

    /// <summary>Ids, totals and prices the model was shown for this voice note: a rewrite may legitimately use them ("pichla order" -> "edit order 105").</summary>
    private static IReadOnlySet<long> KnownVoiceNumbers(IEnumerable<Order> recent, IEnumerable<CatalogEntry> catalog, IEnumerable<string>? recentChatTexts = null)
    {
        var known = new HashSet<long>();
        // The seller's last few messages (and the bot's echo of what it heard) are part of the conversation: "chadar wali product add karo" right after a note that gave
        // the price, cost and stock may use those numbers without saying them again.
        foreach (var chatText in recentChatTexts ?? Array.Empty<string>())
        {
            foreach (var amount in SpokenNumbers.Amounts(chatText)) known.Add(amount);
            foreach (var count in SpokenNumbers.SmallCounts(chatText)) known.Add(count);
        }
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
        // A phone dictated in groups ("0312 4891 718") may come back as one run ("03124891718"): compare both sides joined.
        transcript = JoinSpacedPhones(transcript);
        rewritten = JoinSpacedPhones(rewritten);
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

        // "shayad 12 cancel kar dun", "agar ho sake to price 1500": a hedge or a condition is not an instruction, so it may not become a step
        // that changes money or an order. Pauses ("...") and a question mark are not hedges: they appear in self-corrections and mixed sentences.
        if (HedgeWord.IsMatch(transcript) && rewritten.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(ActionRisk.IsRiskyStep))
            return "the transcript is a hedge, condition or question but the rewrite changes money or an order";
        return null;
    }

    private static readonly Regex HedgeWord = new(@"\b(?:agar|shayad|maybe|perhaps|if|soch\w*|dekh\w*|ruk\w*|wait|hmm+|pata\s+nahi|pata\s+nahin)\b",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
    private static readonly Regex SpacedDigitGroups = new(@"\d(?:[ \-]?\d)*", RegexOptions.Compiled);

    /// <summary>Joins digit groups separated by single spaces or hyphens when together they make a 10-13 digit phone number; anything else is left alone.</summary>
    private static string JoinSpacedPhones(string text) =>
        SpacedDigitGroups.Replace(text, m => m.Value.Count(char.IsDigit) is >= 10 and <= 13 ? m.Value.Replace(" ", "").Replace("-", "") : m.Value);

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
            ConversationState.OnboardingIndustry => "Waiting for the kind of shop (clothing/lawn, grocery, cosmetics/jewellery, electronics, food/bakery, hardware or general): a tap or a word like \"kiryana\".",
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
        if (PendingProductSituation(ctx) is { } waitingProduct)
            core += " " + waitingProduct;
        if (ctx.PendingPriceProducts is { Count: > 0 } waiting)
            core += $" The bot is waiting for the price of these new products: {string.Join(", ", waiting)}. A spoken price (one price for all, or one each) is for them: " +
                    "write it as one sentence naming that price and who it is for (\"sab ki price 5000\" or \"chadar 800, dupatte 1200\"), never as a new product.";
        if (ctx.PendingNewProductName is { } unknown)
            core += $" The bot asked about the product \"{unknown}\" which is not in the catalog: 1 = add it as a new product, 2 = it is another name for an existing catalog product (then the seller names which one).";
        return core;
    }

    public async Task HandleImageMessageAsync(string fromPhoneNumber, string mediaId, string? caption, CancellationToken ct = default, DateTime? sentAtUtc = null)
    {
        _sentAtUtc = sentAtUtc;
        _db.MessageLogs.Add(new MessageLog { Phone = fromPhoneNumber, Direction = "inbound", RawText = $"[image {mediaId}] {caption}" });
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);

        if (!seller.OnboardingComplete)
        {
            await ReplyAsync(seller, MessageTemplates.Get("media.003"), ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        if (await TryHandleBillingAsync(seller, session, ctx, "", ct))
        {
            await PersistAsync(session, ctx, ct);
            return;
        }

        // Receipt logo/banner pictures are not paid AI calls, so only order/receipt screenshots count towards the hourly cap.
        if (!CommandParser.TryParseBrandingCaption(caption, out _)
            && !TryTakeUsage(seller, fromPhoneNumber, MediaKind.Image, out var imageDecision, out var imageWait))
        {
            await ReplyAsync(seller, UsageLimitText(seller, MediaKind.Image, imageDecision, imageWait), ct);
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

        // A picture sent with a QUESTION ("yeh color milega?", "kapra kaun sa hai?") is a question, not an order: no draft is made and any draft in progress stays.
        if (IsQuestionCaption(caption))
        {
            await ReplyAsync(seller, MessageTemplates.Get("media.009"), ct);
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
            await ReplyAsync(seller, MessageTemplates.Get("media.004", header), ct);
            return;
        }

        ctx.ReceiptCandidateOrderIds = candidates.Select(o => o.Id).ToList();
        ctx.ReceiptAmount = receipt.Amount;
        ctx.ReceiptProvider = receipt.Provider;
        ctx.ReceiptTransactionId = receipt.TransactionId;
        SetState(session, ConversationState.AwaitingReceiptOrderChoice);
        await _sender.SendButtonsMessageAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("media.005", header),
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
                else await ReplyAsync(seller, MessageTemplates.Get("media.006"), ct);
                return;
            }
            await ReplyAsync(seller, MessageTemplates.Get("media.007"), ct);
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
            MessageTemplates.Get("media.008", order.Id, Formatters.Money(ctx.ReceiptAmount ?? order.Total), (ctx.ReceiptProvider is null ? "" : $", {ctx.ReceiptProvider}")), ct);
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

    private static readonly Regex QuestionCaption = new(
        @"\?|؟|^\s*(?:kya|kia|kaun|konsa|kaunsa|kitna|kitne|kab|kahan|is\s+ka|iska|yeh\s+kya|ye\s+kya|available|mil\s+jaye|مل\s*جائے|کیا|کون|کتنا)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A caption that asks something and does not say "order": the picture is a question about a product.</summary>
    private static bool IsQuestionCaption(string? caption) =>
        !string.IsNullOrWhiteSpace(caption) && QuestionCaption.IsMatch(caption) && !Regex.IsMatch(caption, @"\border\b|آرڈر", RegexOptions.IgnoreCase);
}
