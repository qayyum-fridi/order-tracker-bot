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

    /// <summary>Voice note: transcribe, echo what was heard so the seller can catch mistakes, then handle it exactly like typed text.</summary>
    public async Task HandleAudioMessageAsync(string fromPhoneNumber, string mediaId, CancellationToken ct = default)
    {
        if (_transcriber is not { IsConfigured: true })
        {
            await HandleUnsupportedMediaAsync(fromPhoneNumber, "audio", ct);
            return;
        }

        var media = _media is null ? null : await _media.DownloadAsync(mediaId, ct);
        var text = media is null ? null : await _transcriber.TranscribeAsync(media.Value.Bytes, media.Value.MimeType, ct);
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

        var understood = await InterpretVoiceAsync(fromPhoneNumber, text, ct);
        var heard = $"🎤 Maine suna: \"{text}\"";
        if (!string.Equals(understood, text, StringComparison.OrdinalIgnoreCase)) heard += $"\n➡️ Samjha: \"{understood}\"";
        await _sender.SendTextMessageAsync(fromPhoneNumber, heard, ct);
        await HandleIncomingMessageAsync(fromPhoneNumber, understood, ct);
    }

    /// <summary>
    /// One AI call that rewrites the transcript into what the seller would have typed for the bot's current question
    /// ("pehla wala" -> "1", "haan kar do" -> "yes", "mere paas 4 lawn suit 3500" -> "Lawn Suit - 3500"). The rewrite still goes through the
    /// normal deterministic engine; the transcript itself is used when the AI is unavailable or the rewrite drops a number.
    /// </summary>
    private async Task<string> InterpretVoiceAsync(string fromPhoneNumber, string transcript, CancellationToken ct)
    {
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var ctx = SessionContextData.FromJson(seller.Session!.ContextJson);
        var catalog = await LoadCatalogAsync(seller, ct);
        var rewritten = await _ai.InterpretVoiceAsync(new AiVoiceContext
        {
            BusinessName = seller.BusinessName ?? "",
            Situation = DescribeVoiceSituation(seller, ctx),
            CatalogNames = catalog.Select(c => c.Label).ToList()
        }, transcript, ct);
        return IsFaithfulRewrite(transcript, rewritten) ? rewritten!.Trim() : transcript;
    }

    /// <summary>
    /// A rewrite is only trusted if it is not absurdly long and keeps every price/phone-sized number the seller said (runs of 3+ digits).
    /// Small numbers may change on purpose ("4 lawn suits at 3500" -> "Lawn Suit - 3500", "pehla" -> "1"); the "Samjha" echo shows the result.
    /// </summary>
    internal static bool IsFaithfulRewrite(string transcript, string? rewritten)
    {
        if (string.IsNullOrWhiteSpace(rewritten) || rewritten.Length > Math.Max(200, transcript.Length * 3)) return false;
        var kept = LongNumberDigits(rewritten);
        return LongNumberDigits(transcript).All(d => kept.GetValueOrDefault(d.Key) >= d.Value);
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
                "The bot is idle: the seller can type a command (orders today, mark 3 shipped, stock Kurti 20, catalog, delivery 250, receipt) or dictate a customer order.",
            ConversationState.Idle => "The bot is at the start of setup; the seller can say start, pick a language or say \"Setup shuru karein\".",
            ConversationState.OnboardingLanguage or ConversationState.AwaitingLanguageChoice => "Waiting for the seller to choose a language: Roman Urdu, Urdu or English.",
            ConversationState.OnboardingStartChoice => "Waiting for a choice: \"Setup shuru karein\", \"Guide dekhein\" or \"Baad mein karunga\".",
            ConversationState.OnboardingBusinessName => "Waiting for the name of the seller's shop/business.",
            ConversationState.OnboardingOptionalDetails => "Waiting for the shop's city, business type and Instagram handle, or \"skip\".",
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
            ConversationState.AwaitingClarificationChoice or ConversationState.AwaitingOrderGroupingChoice or ConversationState.AwaitingRuntimeFilterChoice
                or ConversationState.AwaitingBroadcastAudienceChoice or ConversationState.AwaitingBroadcastChannelChoice or ConversationState.AwaitingReceiptOrderChoice
                or ConversationState.AwaitingSupportQueryPick => "Waiting for a numbered choice.",
            _ => $"The bot is in the step \"{state}\" and expects a short typed answer."
        };

        if (ctx.ClarificationOptions is { Count: > 0 } options)
            core += " Options offered: " + string.Join(" | ", options.Select((o, i) => $"{i + 1}) {o}")) + ".";
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
