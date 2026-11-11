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
        _turnMissed = false;
        _turnTracked = false;
        _lastReply = null;
        await HandleAudioCoreAsync(fromPhoneNumber, mediaId, messageId, replyToId, ct);
        await TrackMissesAsync(fromPhoneNumber, ct);
        await SpeakReplyAsync(fromPhoneNumber, ct);
        _logger?.LogInformation("Voice note: handled in {TotalMs} ms, reply included", handling.ElapsedMilliseconds);

        // The actions this note made are remembered for "undo note". A note that made none (a refused reply) keeps the earlier range.
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var endLogId = await NewestActionLogIdAsync(seller.Id, ct);
        if (endLogId <= startLogId) return;
        var ctx = SessionContextData.FromJson(seller.Session!.ContextJson);
        ctx.LastVoiceNoteStartLogId = startLogId;
        ctx.LastVoiceNoteEndLogId = endLogId;
        ctx.LastVoiceNoteMessageId = messageId;
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

    /// <summary>Set while a voice note that replies to the last note is handled: the one open order that note made.</summary>
    private int? _voiceCorrectionOrderId;

    /// <summary>The single open order made by the last voice note when <paramref name="replyToId"/> is that note; null otherwise (none, several, undone, cancelled).</summary>
    private async Task<int?> OrderOfVoiceNoteAsync(Seller seller, SessionContextData ctx, string replyToId, CancellationToken ct)
    {
        if (ctx.LastVoiceNoteMessageId != replyToId || ctx.LastVoiceNoteEndLogId <= ctx.LastVoiceNoteStartLogId) return null;
        var ids = await _db.ActionLogs
            .Where(l => l.SellerId == seller.Id && l.Id > ctx.LastVoiceNoteStartLogId && l.Id <= ctx.LastVoiceNoteEndLogId && !l.Undone && l.OrderId != null)
            .Select(l => l.OrderId!.Value).Distinct().ToListAsync(ct);
        if (ids.Count != 1) return null;
        var id = ids[0];
        var open = await _db.Orders.AnyAsync(o => o.Id == id && o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned, ct);
        return open ? id : null;
    }

    // WhatsApp voice notes are about 2 KB per second: under ~1 s is silence or a tap, over ~3 MB is a recording of many minutes.
    internal const int MinVoiceBytes = 2000;
    internal const int MaxVoiceSeconds = 300;          // 5 minutes: longer is costly to transcribe and cannot be rewritten into at most 8 steps within the 3-minute handling limit
    internal const int MaxVoiceBytes = 750 * 1024;     // the same limit by size (~2.5 KB per second) for a file whose length cannot be read

    // The length in the file header is the sender's own claim, so it is never trusted alone: a file over this size is refused whatever it says
    // (5 minutes of even a high-bitrate Opus note is well under it).
    internal const int MaxVoiceHardBytes = 2 * 1024 * 1024;

    internal static bool VoiceTooLong(byte[] audio) =>
        audio.Length > MaxVoiceHardBytes
        || (AudioInfo.OggOpusSeconds(audio) is { } seconds ? seconds > MaxVoiceSeconds : audio.Length > MaxVoiceBytes);

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
        // so nothing is created again. When that note made exactly one open order, the reply is read as a correction of it (edit order N, behind
        // the usual YES); otherwise this is said, not guessed.
        _voiceCorrectionOrderId = null;
        var replyCtx = SessionContextData.FromJson(seller.Session!.ContextJson);
        if (replyToId is not null && replyCtx.ProcessedVoiceNoteIds?.Contains(replyToId) == true)
        {
            _voiceCorrectionOrderId = await OrderOfVoiceNoteAsync(seller, replyCtx, replyToId, ct);
            if (_voiceCorrectionOrderId is null)
            {
                await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get("voice-confirm.009"), ct);
                await _db.SaveChangesAsync(ct);
                return;
            }
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
        // An empty or tiny file has nothing to hear, and an oversized one is a long recording the speech model would charge in full: neither is transcribed.
        // The length is read from the Ogg Opus file itself (5 minutes at most); any other format falls back to the file size.
        if (media is { } audio && (audio.Bytes.Length < MinVoiceBytes || VoiceTooLong(audio.Bytes)))
        {
            VoiceStats.Record(audio.Bytes.Length < MinVoiceBytes ? "refused_too_short" : "refused_too_long");
            await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get(audio.Bytes.Length < MinVoiceBytes ? "voice-confirm.018" : "voice-confirm.019"), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        _lastAudioHash = media is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media.Value.Bytes));
        if (_lastAudioHash is not null && SessionContextData.FromJson(seller.Session!.ContextJson).ProcessedVoiceHashes?.Contains(_lastAudioHash) == true)
        {
            VoiceStats.Record("duplicate_note");
            await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get("voice-undo.003"), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        var vocabularyStartMs = voiceClock.ElapsedMilliseconds;
        // The catalog is read once and shared by the speech hints and the rewrite. The rewrite's own reads (orders, customers, chat) run
        // while the transcription request is in flight; the transcription uses no database, so the DbContext is never used twice at once.
        var catalog = media is null ? null : await LoadCatalogAsync(seller, ct);
        var vocabulary = catalog is null ? null : await BuildVoiceVocabularyAsync(seller, catalog, ct);
        var vocabularyMs = voiceClock.ElapsedMilliseconds - vocabularyStartMs;
        var transcription = media is null ? Task.FromResult<string?>(null) : _transcriber.TranscribeAsync(media.Value.Bytes, media.Value.MimeType, vocabulary, ct);
        var prefetch = catalog is null ? null : await LoadVoiceContextAsync(seller, catalog, ct);
        var text = await transcription;
        // An amount word the bot cannot read refuses the whole note. The speech model may have misheard it, so a second model (when one is set up)
        // hears the same audio once; its text is used only if every amount word in it is readable.
        if (text is not null && media is { } heardAudio && SpokenNumbers.UnknownAmountWords(text).Count > 0)
        {
            UnknownNumberWordLog.Record(SpokenNumbers.UnknownAmountWords(text));
            VoiceStats.Record("second_opinion_asked");
            var second = await _transcriber.TranscribeSecondOpinionAsync(heardAudio.Bytes, heardAudio.MimeType, vocabulary, ct);
            if (second is not null && SpokenNumbers.UnknownAmountWords(second).Count == 0)
            {
                _logger?.LogInformation("Voice note: the second speech model read every amount word the first one could not");
                text = second;
                VoiceStats.Record("second_opinion_used");
            }
        }
        var transcribedMs = voiceClock.ElapsedMilliseconds;
        VoiceStats.Record(text is null ? "transcription_failed" : "note_transcribed");
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
        if (_voiceCorrectionOrderId is { } correcting) heard += $"\n{MessageTemplates.Get("voice-confirm.015", correcting)}";
        // Variants (sizes / colours) that the seller says are in a sheet: the voice rewrite keeps only the product, so the variants
        // would be lost. Nothing is saved; the seller sends the file and the import handles the rows.
        if (VariantsInFile.IsMatch(text) && SessionContextData.FromJson(seller.Session!.ContextJson).PendingProductJson is null)
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber, $"{heard}\n\n{MessageTemplates.Get("commands.053")}", ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // Features:ModelFirst: the transcript is read by the one understanding call. The echo is sent only when that call's action is going to run.
        if (_features.ModelFirst is "shadow" or "on")
        {
            var modelCtx = SessionContextData.FromJson(seller.Session!.ContextJson);
            if (await TryModelFirstAsync(seller, seller.Session!, modelCtx, text, ct, fromVoice: true,
                    beforeRun: () => _sender.SendTextMessageAsync(fromPhoneNumber, heard, ct)))
            {
                await PersistAsync(seller.Session!, modelCtx, ct);
                return;
            }
        }

        // A transcript that is only a phone number needs no rewrite (a second, slow model call): it is the answer as typed.
        var joined = JoinSpacedPhones(text.Trim().Trim('.', '!', '?', '،', '۔', ' '));
        _droppedVoiceSteps.Clear();
        _voiceChatRemark = false;
        var decision = joined.All(c => char.IsDigit(c) || c == '+') && IsPlausiblePhone(joined)
            ? TurnDecision.Execute(new[] { joined })
            : await InterpretVoiceAsync(seller, text, ct, prefetch);
        // The rewrite left nothing to run while idle ("naya product" said aloud): the intent catalog may still place the sentence.
        if (decision.Steps.Count == 0 && decision.Kind != DecisionKind.Reject && !_voiceChatRemark && seller.Session!.State == ConversationState.Idle
            && await RouteIntentAsync(text, ct, seller) is { } routed)
        {
            _droppedVoiceSteps.Clear();
            // The routed command passes the same amount guard as a rewrite: a figure the seller never said must not be run (the model fills slots).
            decision = routed.Command is not null
                ? DecideRewrite(text, new[] { routed.Command }, null)
                : TurnDecision.Clarify(RoutedReply(routed));
        }
        VoiceStats.Record("decision_" + decision.Kind.ToString().ToLowerInvariant());
        if (decision.Kind != DecisionKind.Execute) LogNotUnderstood("voice-" + decision.Kind.ToString().ToLowerInvariant(), seller.Session!.State, text);
        if (decision.Kind != DecisionKind.Execute) await RecordUnplacedAsync(seller, "voice_" + decision.Kind.ToString().ToLowerInvariant(), text, null, null, ct);
        // Steps the AI made up or that do not match the words spoken are not run; the seller is told which ones, so nothing vanishes silently.
        // Shown on every reply path (clarify, refuse and run), not only the run path.
        foreach (var (step, template) in _droppedVoiceSteps)
            heard += $"\n{MessageTemplates.Get(template, step)}";
        if (decision.Kind == DecisionKind.Clarify)
        {
            // Clear intent but a detail is missing (which order, the new price): ask for it instead of guessing or failing.
            // When the answers are a few known choices they come as tap buttons, so the seller need not speak again.
            var options = decision.Options ?? Array.Empty<string>();
            if (options.Count > 0)
            {
                // The taps stand for a question the bot is now waiting on, the same as a typed clarification: store them, so a tap
                // is read as its number. Sent as plain buttons, a tap was only a new message and got the generic fallback.
                var voiceSession = seller.Session!;
                var voiceCtx = SessionContextData.FromJson(voiceSession.ContextJson);
                voiceCtx.ClarificationOptions = options.ToList();
                SetState(voiceSession, ConversationState.AwaitingClarificationChoice);
                voiceSession.ContextJson = voiceCtx.ToJson();
                await SendChoicesAsync(fromPhoneNumber, $"{heard}\n\n❓ {decision.Message}", options.Select(o => new ChoiceOption(o, o)).ToList(), ct);
                await _db.SaveChangesAsync(ct);
                return;
            }
            if (await ReaskIndustryAfterUnclearVoiceAsync(fromPhoneNumber, seller, heard, ct)) return;
            await SendChoicesAsync(fromPhoneNumber, $"{heard}\n\n❓ {decision.Message}", VoiceFallbackFor(seller).ToList(), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (decision.Kind == DecisionKind.Reject)
        {
            if (await ReaskIndustryAfterUnclearVoiceAsync(fromPhoneNumber, seller, heard, ct)) return;
            // Unsafe to run and not safe to offer as choices: say nothing changed. The taps are only safe reads (orders, menu), never an approval.
            await SendChoicesAsync(fromPhoneNumber, $"{heard}\n\n{decision.Message}", VoiceFallbackFor(seller), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        if (decision.Kind == DecisionKind.Confirm)
        {
            // Only an idle, set-up seller can park steps; mid-flow the plain "say it again" is safer than replacing the open question.
            if (seller.Session!.State != ConversationState.Idle || !seller.OnboardingComplete)
            {
                await SendChoicesAsync(fromPhoneNumber, $"{heard}\n\n{UnfaithfulQuestion}", VoiceFallbackChoices.ToList(), ct);
                await _db.SaveChangesAsync(ct);
                return;
            }
            var understoodSteps = string.Join("  →  ", decision.Steps.Select(s => $"\"{s}\""));
            var understood = "\n" + MessageTemplates.Get("voice-heard.002", understoodSteps);
            await ParkVoiceStepsAsync(fromPhoneNumber, seller, decision.Steps, $"{heard}{understood}\n\n{decision.Message}", ct);
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
            heard += "\n" + MessageTemplates.Get("voice-heard.002", string.Join("  →  ", steps.Select(s => $"\"{s}\"")));
        if (prefetch is not null)
            foreach (var (nameHeard, nameMatched) in NameMatches(text, steps, prefetch.CustomerNames.Concat(prefetch.Catalog.Select(c => c.Label))).Take(3))
                heard += $"\n{MessageTemplates.Get("voice-confirm.020", nameHeard, nameMatched)}";

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
            await ParkVoiceStepsAsync(fromPhoneNumber, seller, risky, heard, ct, text);
            return;
        }

        // Mixed note: the safe steps run first. The risky ones wait for a YES only when the session is idle again; an open order draft keeps its state.
        await RunVoiceStepsAsync(fromPhoneNumber, safe, _sentAtUtc, ct);
        var after = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        if (after.Session!.State == ConversationState.Idle)
        {
            await ParkVoiceStepsAsync(fromPhoneNumber, after, risky, heard, ct, text);
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

    /// <summary>While the bot still waits on a yes / no question (reset, cancel, ...), a refused voice note leaves that question open: the taps are Yes / No, not unrelated reads.</summary>
    private static readonly ChoiceOption[] VoiceYesNoChoices =
    {
        new("Yes", "Yes"),
        new("No", "No"),
    };

    internal static IReadOnlyList<ChoiceOption> VoiceFallbackFor(Seller seller)
    {
        var allowed = AllowedVoiceActions(seller, seller.Session!.State);
        return allowed.Contains("yes") && allowed.Contains("no") ? VoiceYesNoChoices : VoiceFallbackChoices;
    }

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
    private async Task<List<string>> BuildVoiceVocabularyAsync(Seller seller, IReadOnlyList<CatalogEntry> catalog, CancellationToken ct)
    {
        // Bot vocabulary first (the keyword list is capped): "catalog mein add kar dein" was heard as "address mein".
        var names = new List<string> { "catalog", "product", "products", "order", "delivery" };
        if (!string.IsNullOrWhiteSpace(seller.BusinessName)) names.Add(seller.BusinessName);
        // The speech model takes about 50 keywords and drops the tail, so the order matters: products the seller really sells (most often, then most
        // recently, from their last 200 orders), then the customers with the latest orders, then the rest of the catalog and the older customers.
        var sold = await _db.Orders.Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled).OrderByDescending(o => o.Id).Take(200)
            .SelectMany(o => o.Items).Select(i => i.ProductNameSnapshot).ToListAsync(ct);
        var soldNames = sold.Select((name, index) => (Name: VariantSuffix.Replace(name ?? "", "").Trim(), Index: index)).Where(x => x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.Index)).Select(g => g.Key).Take(20).ToList();
        var customerNames = await _db.Customers.Where(c => c.SellerId == seller.Id && c.DeletedAt == null)
            .OrderByDescending(c => c.Orders.Max(o => (int?)o.Id)).ThenByDescending(c => c.Id).Select(c => c.Name).Take(40).ToListAsync(ct);
        names.AddRange(soldNames);
        names.AddRange(customerNames.Take(15));
        names.AddRange(catalog.Take(30).Select(c => c.Label));
        names.AddRange(customerNames.Skip(15));
        return names;
    }

    /// <summary>A variant in brackets at the end of an order line's product name ("Polo Shirt (white)"): the catalog name is the word to listen for.</summary>
    private static readonly Regex VariantSuffix = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    /// <summary>What the voice rewrite needs from the database, read while the speech model is still transcribing.</summary>
    private sealed record VoiceContextData(
        IReadOnlyList<CatalogEntry> Catalog, List<Order> Recent, List<string> KnownCustomers, List<MessageLog> LastMessages, List<string> Exchanges, List<string> CustomerNames);

    private async Task<VoiceContextData> LoadVoiceContextAsync(Seller seller, IReadOnlyList<CatalogEntry>? catalog, CancellationToken ct)
    {
        catalog ??= await LoadCatalogAsync(seller, ct);
        var recent = await _db.Orders.Include(o => o.Customer).Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id).OrderByDescending(o => o.Id).Take(5).ToListAsync(ct);
        // Only the 40 customers with the latest orders are sent to the model: pick them in SQL instead of loading every customer and order.
        var customerRows = await _db.Customers.Include(c => c.Orders)
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null)
            .OrderByDescending(c => c.Orders.Max(o => (int?)o.Id)).ThenByDescending(c => c.Id).Take(40).ToListAsync(ct);
        var knownCustomers = customerRows.Select(DescribeCustomerForVoice).ToList();
        var lastMessages = await _db.MessageLogs.Where(m => m.Phone == seller.WhatsAppPhoneNumber)
            .OrderByDescending(m => m.Id).Take(6).ToListAsync(ct);
        var exchanges = lastMessages.AsEnumerable().Reverse()
            .Select(m => $"{(m.Direction == "outbound" ? "Bot" : "Seller")}: {(m.RawText.Length > 300 ? m.RawText[..300] : m.RawText)}").ToList();
        return new VoiceContextData(catalog, recent, knownCustomers, lastMessages, exchanges, customerRows.Select(c => c.Name).ToList());
    }

    /// <summary>
    /// Saved names (customers, catalog products) that the rewrite uses but the seller did not say as written: the closest words of the transcript are
    /// the "heard" side of a "heard X, understood Y" note, so a misheard Hussain turned into the saved Hassan is seen before anything runs.
    /// Latin-script transcripts only (a saved name is Latin; Urdu script would differ every time). Plain text distance, no AI.
    /// </summary>
    internal static IReadOnlyList<(string Heard, string Matched)> NameMatches(string transcript, IReadOnlyList<string> steps, IEnumerable<string> names)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(transcript) || Regex.IsMatch(transcript, @"\p{IsArabic}")) return result;
        var spoken = WordsOf(transcript);
        var used = WordsOf(string.Join(' ', steps)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parts = WordsOf(name);
            if (name.Length < 4 || parts.Length is 0 or > 3 || !parts.All(used.Contains)) continue;
            var wanted = string.Join(' ', parts);
            var heard = Enumerable.Range(0, Math.Max(0, spoken.Length - parts.Length + 1)).Select(i => string.Join(' ', spoken.Skip(i).Take(parts.Length))).ToList();
            if (heard.Contains(wanted, StringComparer.OrdinalIgnoreCase)) continue;
            var limit = Math.Max(1, wanted.Length / 3);
            var near = heard.Select(h => (Text: h, Distance: TextDistance.Levenshtein(h, wanted))).Where(x => x.Distance is > 0 && x.Distance <= limit)
                .OrderBy(x => x.Distance).FirstOrDefault();
            if (near.Text is not null) result.Add((near.Text, name));
        }
        return result;
    }

    /// <summary>At the kind-of-shop step an unclear voice note gets the list again (the product-writing help is no use there). True when it did.</summary>
    private async Task<bool> ReaskIndustryAfterUnclearVoiceAsync(string fromPhoneNumber, Seller seller, string heard, CancellationToken ct)
    {
        if (seller.Session!.State != ConversationState.OnboardingIndustry) return false;
        await _sender.SendTextMessageAsync(fromPhoneNumber, heard, ct);
        await AskIndustryAsync(seller, null, ct);
        await _db.SaveChangesAsync(ct);
        return true;
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

    // Plain, non-risky commands whose typed form is the whole instruction: spoken as digits and a clear sentence, the transcript runs as typed (no rewrite call).
    private static readonly HashSet<CommandKind> DirectVoiceCommands = new()
    {
        CommandKind.Stock, CommandKind.Expense, CommandKind.ExpenseList, CommandKind.MonthlyNet,
        CommandKind.Profit, CommandKind.DiscountList, CommandKind.OrderDetail, CommandKind.MarkStatus,
    };

    // States that wait for a yes or no: a bare spoken yes / no there needs no rewrite.
    private static readonly HashSet<ConversationState> YesNoVoiceStates = new()
    {
        ConversationState.AwaitingOrderConfirmation, ConversationState.AwaitingCancelConfirmation, ConversationState.AwaitingBulkStatusConfirmation,
        ConversationState.AwaitingDuplicateOrderConfirmation, ConversationState.AwaitingCodCollectedConfirmation, ConversationState.AwaitingResetConfirmation,
        ConversationState.AwaitingDeleteCustomerConfirmation, ConversationState.AwaitingLoyaltyDiscountConfirmation, ConversationState.AwaitingMultiOrderConfirmation,
        ConversationState.AwaitingSupportReplyConfirmation, ConversationState.AwaitingImportConfirmation, ConversationState.AwaitingVoiceConfirmation,
    };

    // Everyday ways to say yes / no out loud while the bot waits on one. Whole-phrase matches only: anything longer, hedged or with another word goes to the rewrite.
    private static readonly HashSet<string> SpokenYes = new(StringComparer.OrdinalIgnoreCase)
    {
        "haan kar do", "han kar do", "ji kar do", "jee kar do", "haan ji", "han ji", "ji haan", "ji han", "jee haan", "bilkul", "bilkul kar do", "theek hai", "thik hai",
        "theek hai kar do", "thik hai kar do", "ok kar do", "okay kar do", "kar do", "kardo", "sahi hai", "yes please", "ہاں کر دو", "جی کر دو", "جی ہاں", "ہاں جی", "بالکل",
        "ٹھیک ہے", "ٹھیک ہے کر دو", "کر دو", "کردو", "صحیح ہے",
    };

    private static readonly HashSet<string> SpokenNo = new(StringComparer.OrdinalIgnoreCase)
    {
        "nahi karo", "nahi kar do", "mat karo", "mat kar do", "nahi ji", "ji nahi", "nahi bilkul nahi", "bilkul nahi", "rehne do", "rahne do", "chhor do", "ruk jao",
        "نہیں کرو", "نہیں کر دو", "مت کرو", "جی نہیں", "نہیں جی", "بالکل نہیں", "رہنے دو", "چھوڑ دو",
    };

    /// <summary>The typed "yes" / "no" for a spoken everyday phrase while the bot waits on a yes or no; false for anything else (the rewrite then decides).</summary>
    internal static bool TryLocalYesNo(ConversationState state, string transcript, out string step)
    {
        step = "";
        if (!YesNoVoiceStates.Contains(state)) return false;
        var text = Regex.Replace(transcript.Trim().Trim('.', '!', '?', '،', '۔', ' ').ToLowerInvariant(), @"\s+", " ");
        if (text.Length == 0 || text.Split(' ').Length > 3) return false;
        if (SpokenYes.Contains(text)) { step = "yes"; return true; }
        if (SpokenNo.Contains(text)) { step = "no"; return true; }
        return false;
    }

    /// <summary>
    /// True when a transcript can run exactly as typed, skipping the AI rewrite: a short sentence with no hedge, reversal, spoken or retracted amount, that is a bare
    /// yes / no for a state waiting on one, or parses as a plain non-risky command (stock Kurti 20, expense 500 petrol, delivery 250). Anything else is rewritten.
    /// </summary>
    internal static bool CanRunVoiceAsTyped(ConversationState state, string transcript)
    {
        var text = transcript.Trim().Trim('.', '!', '?', '،', '۔', ' ');
        if (text.Length == 0 || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8) return false;
        if (YesNoVoiceStates.Contains(state) && (CommandParser.IsAffirmative(text) || CommandParser.IsNegative(text))) return true;
        if (ReversalWord.IsMatch(text) || HedgeWord.IsMatch(text)) return false;
        if (SpokenNumbers.WordAmounts(text).Count > 0 || SpokenNumbers.UnknownAmountWords(text).Count > 0
            || SpokenNumbers.RetractedAmounts(text).Count > 0 || SpokenNumbers.NegatedAmounts(text).Count > 0) return false;
        if (TurnGate.DecideTyped(text).Kind != DecisionKind.Execute || ActionRisk.IsRiskyStep(text)) return false;
        return CommandParser.TryParse(text) is { } command && DirectVoiceCommands.Contains(command.Kind);
    }

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

    private static readonly System.Text.RegularExpressions.Regex SpokenYesWords = new(
        @"(?<![\p{L}\p{N}])(?:yes|yeah|yep|haan|han|hanji|ha|ji|jee|ok|okay|bilkul|ہاں|جی|بالکل)(?![\p{L}\p{N}])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex SpokenNoWords = new(
        @"(?<![\p{L}\p{N}])(?:no|nope|nahi|nahin|nhi|nai|mat|cancel|stop|ruko|rukiye|نہیں|نہ|مت|رکو|رکیں|کینسل)(?![\p{L}\p{N}])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Most catalog names sent to the voice rewrite: a very large catalog made the prompt slow and let the model pick names that were not spoken.</summary>
    internal const int VoiceCatalogLimit = 120;

    /// <summary>
    /// The catalog names the voice rewrite is shown. Up to <see cref="VoiceCatalogLimit"/> all of them (nothing changes for a normal shop); a larger catalog sends first
    /// the names that share a word with the transcript, then the rest in their order. The model still maps a misheard word ("soor" for suit) to a name it is shown.
    /// </summary>
    internal static List<string> VoiceCatalogNames(IReadOnlyList<string> labels, string transcript)
    {
        if (labels.Count <= VoiceCatalogLimit) return labels.ToList();
        var spoken = System.Text.RegularExpressions.Regex.Split(transcript.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 3).ToHashSet();
        bool Shares(string label) => System.Text.RegularExpressions.Regex.Split(label.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Any(spoken.Contains);
        return labels.Where(Shares).Concat(labels.Where(l => !Shares(l))).Take(VoiceCatalogLimit).ToList();
    }

    /// <summary>
    /// "yes" / "no" for a short spoken answer, while the question on screen is a yes / no one. Only when exactly one of the two is said, there is
    /// no digit, no other command in it and it is at most 8 words; anything else is left to the rewrite.
    /// </summary>
    internal static string? SpokenYesNo(string transcript, IReadOnlyList<string> allowedActions)
    {
        if (!allowedActions.Contains("yes") || !allowedActions.Contains("no")) return null;
        var text = transcript.Trim();
        if (text.Length == 0 || text.Any(char.IsDigit)) return null;
        var words = System.Text.RegularExpressions.Regex.Split(text, @"[\s,.!?،۔؟]+").Count(w => w.Length > 0);
        if (words > 8) return null;
        if (CommandParser.TryParse(text) is not null) return null;
        var yes = SpokenYesWords.IsMatch(text);
        var no = SpokenNoWords.IsMatch(text);
        if (yes == no) return null;
        // "nahi" sits in many sentences ("samajh nahi aa raha"): a NO is read only from a very short answer, since a wrong NO drops the draft.
        return yes ? "yes" : words <= 3 ? "no" : null;
    }

    /// <summary>True while the bot shows a numbered list the seller answers with a number (a menu pick, or the "naya product / doosra naam" and similar order questions).</summary>
    private static bool AwaitsNumberedAnswer(Seller seller)
    {
        var state = seller.Session!.State;
        if (NumberedChoiceStates.Contains(state)) return true;
        if (state != ConversationState.AwaitingOrderMissingFields) return false;
        var ctx = SessionContextData.FromJson(seller.Session.ContextJson);
        return ctx.PendingNewProductName is not null || ctx.PendingMissingField is "ProductChoice" or "VariantChoice" or "PriceChoice" or "DeliveryChoice";
    }

    /// <summary>Longest voice note (in words) that is first tried as a single intent, as said, before the rewrite.</summary>
    private const int MaxSpokenRouteWords = 12;

    private async Task<TurnDecision> InterpretVoiceAsync(Seller seller, string transcript, CancellationToken ct, VoiceContextData? prefetch = null)
    {
        if (NumberedChoiceStates.Contains(seller.Session!.State) && SpokenNumbers.ChoiceNumber(transcript) is { } pick)
            return TurnDecision.Execute(new[] { pick.ToString() });
        // One new product is waiting for its price and the note is short and names exactly one amount ("price dedh sau rupay"): that amount is the price. It is
        // read by the same amount reader as typed text and goes through the same price step, so it needs no rewrite that a gate could refuse. A note with a hedge, a
        // taken-back amount or an unreadable amount word, or a longer sentence (which may be about something else), still goes the normal way.
        if (seller.Session!.State == ConversationState.Idle
            && SessionContextData.FromJson(seller.Session.ContextJson).PendingPriceProducts is { Count: 1 }
            && transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6
            && !HedgeWord.IsMatch(transcript)
            && SpokenNumbers.RetractedAmounts(transcript).Count == 0 && SpokenNumbers.NegatedAmounts(transcript).Count == 0
            && SpokenNumbers.UnknownAmountWords(transcript).Count == 0
            && SpokenNumbers.Amounts(transcript) is { Count: 1 } onlyAmount)
            return TurnDecision.Execute(new[] { onlyAmount.Single().ToString(System.Globalization.CultureInfo.InvariantCulture) });
        // A greeting or small talk is not an order or a command: say so plainly, and do not let the model invent steps from it.
        if (IsSmallTalkOnly(transcript))
            return TurnDecision.Clarify(MessageTemplates.Get("voice.smalltalk"));
        // A plain read-only command ("orders today", "menu") needs no rewrite: it runs as spoken, the same as a typed one.
        if (CommandParser.TryParse(transcript) is { } readOnly && ReadOnlyVoiceCommands.Contains(readOnly.Kind))
            return TurnDecision.Execute(new[] { transcript });
        // A spoken "yeh expense hai" right after a picture is the label for it: it runs as typed, exactly like the typed word, with no rewrite to mishear it.
        if (SessionContextData.FromJson(seller.Session!.ContextJson) is { PendingPictureMediaIds.Count: > 0 } pictureCtx && pictureCtx.PendingPictureAt is not null
            && PictureLabels.TryParseReply(transcript, out _))
            return TurnDecision.Execute(new[] { transcript });
        if (TryLocalYesNo(seller.Session!.State, transcript, out var yesNoStep))
            return TurnDecision.Execute(new[] { yesNoStep });
        if (CanRunVoiceAsTyped(seller.Session!.State, transcript))
            return TurnDecision.Execute(new[] { transcript });
        // "setup mein abhi kya baqi hai?", "ab kya karna hai": a request for the next step is answered with the tip for the current step, as typed.
        if (CommandParser.IsGuidanceRequest(transcript))
            return TurnDecision.Execute(new[] { transcript });
        // While the bot waits for yes / no, a short spoken answer with filler ("او بھائی میں نے ہاں بولا تھا") is the answer: no model call, so no model can turn it into something else.
        var allowedNow = AllowedVoiceActions(seller, seller.Session!.State);
        if (SpokenYesNo(transcript, allowedNow) is { } yesNo)
            return TurnDecision.Execute(new[] { yesNo });
        // The word rules above are only a fast path. For any other answer to a yes / no question, ask what it MEANS (any dialect, any filler): the model may
        // answer only yes / no / other, so it cannot make up a step. "other" (a different request, or unclear) goes on to the normal rewrite.
        if (allowedNow.Contains("yes") && allowedNow.Contains("no") && !transcript.Any(char.IsDigit) && transcript.Length <= 300)
        {
            var asked = await _db.MessageLogs.Where(m => m.Phone == seller.WhatsAppPhoneNumber && m.Direction == "outbound")
                .OrderByDescending(m => m.Id).Select(m => m.RawText).FirstOrDefaultAsync(ct) ?? "";
            var meaning = await _ai.ClassifyYesNoAsync(asked, transcript, ct);
            _logger?.LogInformation("Voice yes/no: the model read the answer as {Meaning}", meaning ?? "unavailable");
            if (meaning is "yes" or "no") return TurnDecision.Execute(new[] { meaning });
        }
        // Intent first (idle seller): the kind of note decides the narrowest step. The router and the amount guard are the ones the typed path already uses.
        if (_features.VoiceIntentFirst && seller.OnboardingComplete && seller.Session!.State == ConversationState.Idle)
        {
            var kind = await _ai.ClassifyVoiceKindAsync(transcript, ct);
            _logger?.LogInformation("Voice kind: the model read the note as {Kind}", kind ?? "unavailable");
            if (kind == "chat")
            {
                _voiceChatRemark = true;
                return TurnDecision.Clarify(MessageTemplates.Get("voice.smalltalk"));
            }
            if (kind == "command" && await RouteIntentAsync(transcript, ct, seller) is { } routed)
                return routed.Command is not null
                    ? DecideRewrite(transcript, new[] { routed.Command }, null)
                    : TurnDecision.Clarify(RoutedReply(routed));
        }
        // A short note from an idle seller goes next to the same router the typed text uses, with the words as they were said. The rewrite can change
        // the words ("kitne customers" became "kis tarah ka customer"), so it runs only when nothing routes. The amount guard checks the spoken words.
        if (seller.OnboardingComplete && seller.Session!.State == ConversationState.Idle
            && transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= MaxSpokenRouteWords
            && await RouteIntentAsync(transcript, ct, seller) is { } spoken)
            return spoken.Command is not null
                ? DecideRewrite(transcript, new[] { spoken.Command }, null)
                : TurnDecision.Clarify(RoutedReply(spoken));
        var ctx = SessionContextData.FromJson(seller.Session!.ContextJson);
        var (catalog, recent, knownCustomers, lastMessages, exchanges, _) = prefetch ?? await LoadVoiceContextAsync(seller, null, ct);

        // A spoken "cancel" while an order draft waits is a cancel, whatever the model would make of it ("kis order ko cancel karna hai?").
        if (ctx.PendingOrder is not null && OpensWithLeavePhrase(transcript.Trim().Trim('.', '!', '?', '،', '۔', ' ')))
            return TurnDecision.Execute(new[] { "cancel" });

        var rewriteClock = System.Diagnostics.Stopwatch.StartNew();
        VoiceStats.Record("rewrite_called");
        var result = await _ai.InterpretVoiceAsync(new AiVoiceContext
        {
            BusinessName = seller.BusinessName ?? "",
            Situation = DescribeVoiceSituation(seller, ctx) + (_voiceCorrectionOrderId is { } fixId
                ? $" This note is a reply to the seller's previous voice note and corrects the order that note made, Order #{fixId}: " +
                  $"never create a new order from it; return the steps \"edit order {fixId}\", then the edit lines (price 1 = 1500, qty 1 = 4, remove 2, delivery 250...), then \"done\"."
                : ""),
            AllowedActions = AllowedVoiceActions(seller, seller.Session!.State).ToList(),
            CatalogNames = VoiceCatalogNames(catalog.Select(c => c.Label).ToList(), transcript),
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
            result = KeepSpokenSteps(transcript, result, known, AwaitsNumberedAnswer(seller));
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
                // A numbered list is waiting and the model read the spoken answer as one of its numbers ("jee haan, yeh naya product hai" = 1): that is the answer, not an
                // invented amount. The number picks an option the bot itself offered, so nothing is saved by it that the next question does not still confirm.
                if (steps.Count == 1 && AwaitsNumberedAnswer(seller) && System.Text.RegularExpressions.Regex.IsMatch(steps[0].Trim(), @"^[1-9]$"))
                    return TurnDecision.Execute(steps);
                // A rewrite that changes what was said is not run, and neither is the raw transcript: the seller is asked again.
                // Reason only (never the transcript text): makes refused rewrites visible in the logs.
                if (WhyUnfaithful(transcript, string.Join("\n", steps), known) is { } why)
                    _logger?.LogWarning("Voice rewrite refused: {Reason} (risky={Risky})", why, StepsAreRisky(steps));
                var whole = DecideRewrite(transcript, CorrectMarginQuestion(transcript, CorrectAccountReset(transcript, steps)), known);
                // The model picks the spans, so the fallback never trusts them alone: it is closed to a refused (risky) note, to any risky step, and to a
                // transcript with a hedge, a taken-back or refused amount or an unreadable amount word, all of which the whole-note gate sees and a span can hide.
                if (whole.Kind is DecisionKind.Clarify or DecisionKind.Confirm && steps.Count == result.Spans.Count
                    && !HedgeWord.IsMatch(transcript)
                    && SpokenNumbers.RetractedAmounts(transcript).Count == 0 && SpokenNumbers.NegatedAmounts(transcript).Count == 0
                    && SpokenNumbers.UnknownAmountWords(transcript).Count == 0
                    && StepsThatStandOnTheirOwn(transcript, steps, result.Spans, known) is { Keep.Count: > 0 } standing
                    && !StepsAreRisky(standing.Keep))
                {
                    VoiceStats.Record("partial_fallback");
                    foreach (var step in standing.Dropped) _droppedVoiceSteps.Add((step, "voice-confirm.007"));
                    return TurnDecision.Execute(CorrectMarginQuestion(transcript, CorrectAccountReset(transcript, standing.Keep)));
                }
                return whole;
            }
        }
        // The AI was unavailable: the transcript goes to the deterministic engine, as before, unless it has an amount word the engine cannot read.
        if (result is null) return UnknownAmountDecision(transcript, Array.Empty<string>()) ?? TurnDecision.Execute(new[] { transcript });
        // The AI answered without a usable step: the transcript runs only when it reads as a plain command or answer.
        if (CommandParser.TryParse(transcript) is not null || CommandParser.IsAffirmative(transcript) || CommandParser.IsNegative(transcript))
            return TurnDecision.Execute(new[] { transcript });
        return TurnDecision.Clarify(MessageTemplates.Get("voice.parse", Industry(seller).SimpleProduct));
    }

    /// <summary>
    /// A long note with several tasks: when the whole-note check refuses it, each task is checked against its OWN spoken words (its span) instead, so one doubtful
    /// number does not throw away the others. Tasks that pass run (risky ones still wait for YES); the ones that do not are listed as not done. Applies only when at
    /// least one task failed on its own (here or in <see cref="KeepSpokenSteps"/>), so a task the model left out entirely (which only the whole-note check can see) never lets a partial note through silently.
    /// Returns the passing steps, or null when nothing failed individually or nothing passed.
    /// </summary>
    internal (List<string> Keep, List<string> Dropped)? StepsThatStandOnTheirOwn(string transcript, IReadOnlyList<string> steps, IReadOnlyList<string> spans, IReadOnlySet<long> known)
    {
        var keep = new List<string>();
        var dropped = new List<string>();
        var spoken = SpeechWords(transcript);
        for (var i = 0; i < steps.Count; i++)
        {
            var span = spans[i];
            // The span must be the seller's own words, verbatim: a span the model made up or trimmed proves nothing.
            var ok = !string.IsNullOrWhiteSpace(span)
                     && spoken.Contains(SpeechWords(span))
                     && SpokenNumbers.UnknownAmountWords(span).Count == 0
                     && WhyUnfaithful(span, steps[i], known) is null;
            if (ok) keep.Add(steps[i]); else dropped.Add(steps[i]);
        }
        // Something must have failed on its own: here, or earlier in KeepSpokenSteps (whose dropped task is why the whole-note amounts no longer add up).
        if (keep.Count == 0 || (dropped.Count == 0 && _droppedVoiceSteps.Count == 0)) return null;
        return (keep, dropped);
    }

    /// <summary>Steps removed by <see cref="KeepSpokenSteps"/> for the current voice note; shown to the seller with the echo.</summary>
    private readonly List<(string Step, string Template)> _droppedVoiceSteps = new();

    /// <summary>Set when the note was read as a chat remark: nothing is placed from it, so the idle router fallback must not try either.</summary>
    private bool _voiceChatRemark;

    /// <summary>
    /// Keeps the steps whose span is really in the transcript and whose amounts were said inside that span (or are known to the seller's data).
    /// Steps without a span keep the whole-note check. A dropped step is logged with its reason only, never with the transcript text.
    /// </summary>
    private AiVoiceInterpretation KeepSpokenSteps(string transcript, AiVoiceInterpretation result, IReadOnlySet<long> known, bool listPick = false)
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
            if (!shopNameRename && SpanProblem(transcript, result.Steps[i], span, known, listPick) is { } problem)
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
    private static (string Reason, string Template)? SpanProblem(string transcript, string step, string span, IReadOnlySet<long> known, bool listPick = false)
    {
        var spoken = SpeechWords(span);
        if (spoken.Trim().Length == 0)
        {
            // A bare list number with no span ("2" for a correction read as a list pick) must have been said as digits.
            if (!System.Text.RegularExpressions.Regex.IsMatch(step.Trim(), @"^\d{1,2}$")) return null;
            // While a numbered list is on the seller's screen, the model reading an answer in words ("haan, naya product hai") as that list number IS the answer.
            if (listPick) return null;
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
        // "contact number bata sakte hain?" is a question about a record, not a request for a person: "contact support" is only for a seller who asks for support.
        if (steps.Any(st => st.TrimStart().StartsWith("contact support", StringComparison.OrdinalIgnoreCase)) && !SupportWords.IsMatch(transcript))
            steps = steps.Select(st => st.TrimStart().StartsWith("contact support", StringComparison.OrdinalIgnoreCase) ? "guide" : st).Distinct().ToList();
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

    private static readonly System.Text.RegularExpressions.Regex SupportWords = new(
        @"support|sapport|team|insaan|insan|founder|human|baat karn|baat karao|سپورٹ|ٹیم|انسان|بات کر",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

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
        if (WhyUnfaithful(transcript, string.Join("\n", steps), knownNumbers) is not { } why) return TurnDecision.Execute(steps);
        // A doubt about one named number is said as such. Harmless steps wait for a YES (the seller sees what was understood);
        // risky ones are still refused. Any other doubt (a refused item, a dropped condition, a hedge) keeps the plain "say it again".
        // Where the bot has a best guess, it shows it and asks for one tap ("sahi hai?") instead of making the seller say everything again; the button names the
        // amount or order, so the seller approves the exact command. Two doubts stay a plain "say it again": a rewrite that is empty or a paragraph, and a value the
        // seller explicitly refused ("teen nahi, do"), which must never come back as the proposal.
        // A hedged risky step ("shayad order 12 cancel kar dun") is also refused: a hesitant seller must not be nudged into a cancel by a button.
        if (why is "empty rewrite" or "rewrite too long" || why.Contains("was refused") || (HedgeWord.IsMatch(transcript) && StepsAreRisky(steps)))
            return StepsAreRisky(steps) ? TurnDecision.Reject(RejectedUnfaithful) : TurnDecision.Clarify(UnfaithfulQuestion);
        var number = UncheckedNumber(why);
        return TurnDecision.Confirm(steps, number is null ? MessageTemplates.Get("voice-confirm.030") : MessageTemplates.Get("voice-confirm.014", number));
    }

    private static readonly Regex UncheckedNumberReason = new(@"^(?:spoken amount|amount|count) (\d+) (?:is missing|appears)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The number a "missing" or "not said" guard reason is about; null for every other reason (including a number the seller refused).</summary>
    private static string? UncheckedNumber(string reason) =>
        UncheckedNumberReason.Match(reason) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The reply for a note with an amount word the bot cannot read; null when every amount word is known.</summary>
    private static TurnDecision? UnknownAmountDecision(string transcript, IReadOnlyList<string> steps)
    {
        if (SpokenNumbers.UnknownAmountWords(transcript) is not { Count: > 0 } unknown) return null;
        UnknownNumberWordLog.Record(unknown);
        var question = MessageTemplates.Get("voice-confirm.011", unknown[0]);
        if (StepsAreRisky(steps))
        {
            // One risky step with ONE amount (a price change, an advance...) and no hedge: the amount word cannot be read by the list, but the seller can read the amount the
            // bot understood. It is shown with a button that names that amount (YesButtonLabel); nothing runs before the tap. Hedged, several-step or amount-less
            // risky notes are still refused.
            if (steps.Count == 1 && !HedgeWord.IsMatch(transcript) && Regex.Matches(steps[0], @"(?<![\d.])\d{3,9}(?![\d.])").Count == 1)
                return TurnDecision.Confirm(steps, MessageTemplates.Get("voice-confirm.030"));
            return TurnDecision.Reject(question);
        }
        // A harmless step (an expense, a counter sale...) whose rewrite carries an amount: the word list cannot check that amount, but the seller can. The command the
        // bot understood is shown with a button that names the amount, and nothing runs until the tap, so an unlisted spelling no longer ends in "say it again".
        // A rewrite with no amount in it (the model may have dropped the unreadable one) is still asked again.
        if (steps.Count > 0 && steps.All(step => Regex.IsMatch(step, @"\d{2,}")))
            return TurnDecision.Confirm(steps, MessageTemplates.Get("voice-confirm.030"));
        return TurnDecision.Clarify(question);
    }

    private static bool StepsAreRisky(IEnumerable<string> steps) => steps.Any(ActionRisk.IsRiskyStep);

    private static readonly string RejectedUnfaithful = MessageTemplates.Get("voice-heard.003");

    // Three different outcomes, three different replies: a rewrite that changed the meaning, a transcript nothing could read, and (above) a rewrite
    // that was used. None of them runs a step.
    private static readonly string UnfaithfulQuestion = MessageTemplates.Get("voice-heard.004");

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

    private static readonly Regex HedgeWord = new(@"\b(?:agar|shayad|maybe|perhaps|if|soch\w*|dekh\w*|ruk\w*|wait|hmm+|pata\s+nahi|pata\s+nahin)\b|(?<!\p{L})(?:شاید|اگر|سوچ\p{L}*|رک(?:و|نا|یں|یے)?|ہمم+|پتا\s+نہیں|پتہ\s+نہیں)(?!\p{L})",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BareAffirmation = new(@"^(yes|y|ha|haan|han|hanji|ji|jee|ok|okay|theek hai|thik hai)[.!]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ReversalWord = new(@"(?:\b(?:lekin|magar|par|but|however|nahi|nahin|nahee|mat|na|no|ruk|ruko|rukna|pehle|phir|cancel|agar|shayad|soch|sochta|sochna|sochun|dekhta|dekhti|dekhungi|hmm|hmmm|baad|abhi|wait|if|maybe)\b)|(?<!\p{L})(?:لیکن|مگر|پر|نہیں|مت|رک(?:و|نا|یں|یے)?|پہلے|پھر|منسوخ|کینسل|اگر|شاید|سوچ\p{L}*|ہمم+|بعد|ابھی)(?!\p{L})|\?|؟|\.\.\.|…", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Comparator = new(@"\bse\s+(?:kam|kum|zyada|zyaada|ziada|upar|neeche|less|more)\b|\bton\s+(?:ghatt|ghat|kam|chhota|zyada|vadh|vadha|upar)\b|\b(?:less|more)\s+than\b|\b(?:under|below|above|over|upto|up\s+to)\b|(?:\d|\b(?:hazaar|hazar|hajar|sau|lakh|k)\b)\s+tak\b|سے\s+(?:کم|کمتر|زیادہ|زیادہ|اوپر|نیچے)(?!\p{L})|(?:\d|ہزار|سو|لاکھ)\s*تک(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RewriteComparator = new(@"\b(?:under|below|above|over|less|more|upto|up\s+to|max|maximum|min|minimum|kam|zyada|tak|ghatt|vadh|vadha)\b|<|>|≤|≥", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DeliveryWord = new(@"\b(?:delivery|shipping|courier|postage)\b|ڈیلیوری|ڈلیوری|شپنگ|کوریئر|کورئیر",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex InclusionWord = new(@"\b(?:including|included|inclusive|incl|shamil)\b|(?<!\p{L})(?:شامل|انکلوڈ(?:ڈ)?)(?!\p{L})",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
    internal static string DescribeVoiceSituation(Seller seller, SessionContextData ctx)
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
                or ConversationState.AwaitingSupportReplyConfirmation => "Waiting for a yes or no confirmation.",
            ConversationState.AwaitingImportConfirmation =>
                "Waiting for yes or no to import the customer/product file the seller just sent (its summary was shown). " +
                "\"yes\", \"haan\", \"import karo\", \"save karo\", and any sentence asking to read, import, save or add that file or its customers (\"ye file import karo\", \"is ko read karein aur customers add karein\") mean \"yes\". " +
                "\"no\", \"cancel\" or \"nahi\" mean \"no\". Never answer with a menu.",
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
        // A photo sent after "photo customers" (etc.) is read as that kind, when it has no caption of its own.
        caption = PhotoCaptionFor(ctx, caption);

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

        // A caption the word list does not know ("Customers", "yeh customer list hai") is read by meaning, like any choice.
        if (!string.IsNullOrWhiteSpace(caption) && !PictureLabels.TryParseCaption(caption, out _, out _)
            && await ResolveChoiceAsync(seller, ctx, caption, PictureTypeChoices, allowNumber: false, ct) is { } captionKind)
            caption = captionKind;

        // A caption naming the type (expense, wages, customer, other) says what the picture is: read it for that type only. "order" continues below.
        if (PictureLabels.TryParseCaption(caption, out var pictureKind, out var pictureNote) && pictureKind != PictureLabels.Order)
        {
            await ApplyPictureLabelAsync(seller, session, ctx, pictureKind, pictureNote, media.Value, ct);
            await PersistAsync(session, ctx, ct);
            await RunAutoStepAsync(fromPhoneNumber, ct);
            return;
        }

        // The first picture without a type word: once, say how to send a clear one and how to label it, so later pictures arrive readable and named.
        if (!ctx.PhotoTipShown)
        {
            ctx.PhotoTipShown = true;
            await ReplyAsync(seller, MessageTemplates.Get("voice-confirm.036"), ct);
        }

        // A screenshot starts fresh: any half-finished draft/prompt is dropped.
        ResetFlowContext(ctx);
        SetState(session, ConversationState.Idle);
        // Without a type word the picture is read as an order or a payment receipt as before, and remembered for a few minutes:
        // a following "expense" / "wages" / "customer" message (typed or spoken) then labels it.
        RememberPicture(ctx, mediaId);

        // Features:ModelFirst: the picture is read by the one understanding call first. When its action is not one of the core jobs, the picture reader below answers.
        if (_features.ModelFirst is "shadow" or "on"
            && await TryModelFirstAsync(seller, session, ctx, caption ?? "", ct,
                image: new AiImageInput(media.Value.Bytes, media.Value.MimeType, caption), fromScreenshot: true))
        {
            await PersistAsync(session, ctx, ct);
            return;
        }

        var catalog = await LoadCatalogAsync(seller, ct);
        var analysis = await _ai.AnalyzeImageAsync(AiContext(seller, catalog), new AiImageInput(media.Value.Bytes, media.Value.MimeType, caption), ct);

        if (analysis.Receipt is { Amount: > 0 } receipt)
            await StartReceiptMatchAsync(seller, session, ctx, receipt, ct);
        else if (analysis.Order is null && (analysis.AiUnavailable || analysis.Intent is "unclear" or "off_topic"))
        {
            // Not an order or a payment receipt: ask what the picture is, with taps, instead of a "samajh nahi aaya". A tap is the type word, and the picture
            // was remembered above, so it is read as that type.
            await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("voice-confirm.029"),
                PictureTypeChoices.Select(c => new ChoiceOption(c.Label, c.Value)).ToList(), ct);
        }
        else
            await HandleAnalysisAsync(seller, session, ctx, analysis, catalog, fromScreenshot: true, ct);

        await PersistAsync(session, ctx, ct);
    }

    // Each value is a type word PictureLabels understands (a test checks it), so a tap labels the remembered picture.
    internal static readonly (string Label, string Value)[] PictureTypeChoices =
    {
        ("Kharcha (bill)", "expense"), ("Tankhwah", "wages"), ("Customer ki detail", "customer"), ("Products ki list", "products"), ("Order", "order"), ("Kuch nahi", "other"),
    };

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
