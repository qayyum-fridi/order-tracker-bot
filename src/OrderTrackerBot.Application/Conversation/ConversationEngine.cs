using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// The message router: one WhatsApp message in, zero or more WhatsApp replies out.
/// Deterministic commands (<see cref="CommandParser"/>) are answered directly from the
/// database. Anything else is handed to the AI assistant to extract an order or ask a
/// clarifying question. A non-Idle <see cref="ConversationState"/> means the seller is
/// mid-flow (onboarding, confirming an order, ...) and takes priority over both.
/// </summary>
public partial class ConversationEngine
{
    private readonly IAppDbContext _db;
    private readonly IAiOrderAssistant _ai;
    private readonly IWhatsAppSender _sender;
    private readonly TranslatingSender _translator;
    private readonly IFounderAlertNotifier _founderAlerts;
    private readonly BillingOptions _billing;
    private readonly IWhatsAppMediaClient? _media;
    private readonly IInstagramClient _instagram;
    private readonly ICatalogSheetImporter? _catalogSheets;
    private readonly IReceiptPdfGenerator? _receiptPdf;
    private readonly IExportFileWriter? _exportWriter;
    private readonly IImportFileReader? _importReader;
    private readonly FeatureOptions _features;
    private readonly IIssueReporter? _issues;
    private readonly INewSellerNotifier? _newSellers;
    private readonly MediaRateLimiter? _mediaLimiter;
    private readonly IAudioTranscriber? _transcriber;
    private readonly MessageLoggingSender _turn;
    private Seller? _turnSeller;
    private SessionContextData? _turnCtx;

    public ConversationEngine(IAppDbContext db, IAiOrderAssistant ai, IWhatsAppSender sender, IFounderAlertNotifier founderAlerts,
        BillingOptions? billing = null, IWhatsAppMediaClient? media = null, IInstagramClient? instagram = null, ICatalogSheetImporter? catalogSheets = null,
        IReceiptPdfGenerator? receiptPdf = null, IExportFileWriter? exportWriter = null, FeatureOptions? features = null,
        IIssueReporter? issues = null, IAudioTranscriber? transcriber = null, IImportFileReader? importReader = null,
        INewSellerNotifier? newSellers = null, MediaRateLimiter? mediaLimiter = null)
    {
        _newSellers = newSellers;
        _mediaLimiter = mediaLimiter;
        _db = db;
        _ai = ai;
        _turn = new MessageLoggingSender(sender, db);
        _sender = _translator = new TranslatingSender(_turn, ai);
        _founderAlerts = founderAlerts;
        _billing = billing ?? new BillingOptions();
        _media = media;
        _instagram = instagram ?? new NullInstagramClient();
        _catalogSheets = catalogSheets;
        _receiptPdf = receiptPdf;
        _exportWriter = exportWriter;
        _issues = issues;
        _transcriber = transcriber;
        _importReader = importReader;
        // Unconfigured (e.g. unit tests) means off; the app registers FeatureOptions with its real defaults.
        _features = features ?? new FeatureOptions { ShortcutButtons = false };
    }

    private static readonly ConversationState[] OnboardingStates =
    {
        ConversationState.OnboardingBusinessName, ConversationState.OnboardingLanguage, ConversationState.OnboardingOptionalDetails,
        ConversationState.OnboardingCatalogSize, ConversationState.OnboardingAddProduct, ConversationState.OnboardingStartChoice
    };

    public async Task HandleIncomingMessageAsync(string fromPhoneNumber, string rawMessage, CancellationToken ct = default)
    {
        _turn.Reset();
        _turnSeller = null;
        _turnCtx = null;
        _stockWarnings.Clear();
        if (await TryHandleVoiceConfirmationAsync(fromPhoneNumber, rawMessage, ct)) return;
        await HandleIncomingCoreAsync(fromPhoneNumber, rawMessage, ct);
        await FlushStockWarningsAsync(fromPhoneNumber, ct);
        await TrySendShortcutBarAsync(fromPhoneNumber, ct);
    }

    private async Task HandleIncomingCoreAsync(string fromPhoneNumber, string rawMessage, CancellationToken ct)
    {
        var message = (rawMessage ?? string.Empty).Trim();
        if (message.Length == 0) return;

        _db.MessageLogs.Add(new MessageLog { Phone = fromPhoneNumber, Direction = "inbound", RawText = message });
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        message = _translator.RestoreButtonLabel(message);

        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);
        _turnSeller = seller;
        _turnCtx = ctx;

        // "kya karun" / "kaise karun" / "?" — a tip for the current step, from any state, without touching it.
        if (CommandParser.IsGuidanceRequest(message))
        {
            await SendContextualGuideAsync(seller, session, ctx, ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        if (session.State != ConversationState.AwaitingResetConfirmation
            && CommandParser.TryParse(message)?.Kind == CommandKind.ResetAccount)
        {
            await StartResetAsync(seller, session, ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        // An incomplete seller normally always routes to the onboarding handler — except a state that
        // has its own handler elsewhere (currently just the guide, reachable as a mid-onboarding escape
        // hatch): otherwise the very next message after opening it would fall to HandleOnboardingAsync's
        // default case and restart onboarding from scratch instead of reaching that handler.
        if (OnboardingStates.Contains(session.State)
            || (!seller.OnboardingComplete && session.State is not (ConversationState.AwaitingGuideStep or ConversationState.AwaitingImportConfirmation)))
        {
            await HandleOnboardingAsync(seller, session, ctx, message, ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        // "reset account" itself is allowed past billing (above), so its YES/NO must be too — otherwise an
        // expired-trial seller sees the paywall instead of the reset.
        if (session.State != ConversationState.AwaitingResetConfirmation
            && await TryHandleBillingAsync(seller, session, ctx, message, ct))
        {
            await PersistAsync(session, ctx, ct);
            return;
        }

        if (session.State is ConversationState.AwaitingOrderConfirmation or ConversationState.AwaitingOrderMissingFields
                or ConversationState.AwaitingOrderGroupingChoice or ConversationState.AwaitingMultiOrderConfirmation
            && await TryLeaveOrderDraftAsync(seller, session, ctx, message, ct))
        {
            await PersistAsync(session, ctx, ct);
            return;
        }

        switch (session.State)
        {
            case ConversationState.AwaitingOrderConfirmation:
                await HandleOrderConfirmationAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingOrderMissingFields:
                await HandleOrderMissingFieldAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingOrderGroupingChoice:
                await HandleOrderGroupingChoiceAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingMultiOrderConfirmation:
                await HandleMultiOrderConfirmationAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingClarificationChoice:
                await HandleClarificationChoiceAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingCancelConfirmation:
                await HandleCancelConfirmationAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingBulkStatusConfirmation:
                await HandleBulkStatusConfirmationAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingDuplicateOrderConfirmation:
                await HandleDuplicateOrderConfirmationAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingCodCollectedConfirmation:
                await HandleCodCollectedConfirmationAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingRuntimeFilterChoice:
                await HandleRuntimeFilterChoiceAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingCustomDateRange:
                await HandleCustomDateRangeAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingBroadcastChannelChoice:
                await HandleBroadcastChannelChoiceAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingBroadcastAudienceChoice:
                await HandleBroadcastAudienceChoiceAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingDiscountDetails:
                await HandleDiscountDetailsAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingResetConfirmation:
                await HandleResetConfirmationAsync(seller, session, message, ct);
                break;
            case ConversationState.AwaitingBusinessInfo:
                await HandleBusinessInfoAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingReceiptOrderChoice:
                await HandleReceiptOrderChoiceAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingDeleteCustomerConfirmation:
                await HandleDeleteCustomerConfirmationAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingLoyaltyDiscountConfirmation:
                await HandleLoyaltyDiscountConfirmationAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingSupportReplyConfirmation:
                await HandleSupportReplyConfirmationAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingSupportReplyEdit:
                await HandleSupportReplyEditAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingLanguageChoice:
                await HandleLanguageChoiceAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingPaymentMethodInput:
                await HandlePaymentMethodInputAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingGuideStep:
                await HandleGuideStepAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingOrderEdit:
                await HandleOrderEditAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingSupportQueryPick:
                await HandleSupportQueryPickAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingImportConfirmation:
                await HandleImportConfirmationAsync(seller, session, ctx, message, ct);
                break;
            default:
                await HandleIdleAsync(seller, session, ctx, message, ct);
                break;
        }

        await PersistAsync(session, ctx, ct);
    }

    private async Task<Seller> LoadOrCreateSellerAsync(string phone, CancellationToken ct)
    {
        var seller = await _db.Sellers
            .Include(s => s.Session)
            .FirstOrDefaultAsync(s => s.WhatsAppPhoneNumber == phone, ct);

        if (seller is null)
        {
            seller = new Seller { WhatsAppPhoneNumber = phone };
            seller.Session = new ConversationSession { State = ConversationState.Idle };
            _db.Sellers.Add(seller);
            await _db.SaveChangesAsync(ct);
            if (_newSellers is not null)
                await _newSellers.SellerRegisteredAsync(phone, await _db.Sellers.CountAsync(ct), ct);
        }

        _translator.Seller = seller;
        return seller;
    }

    public async Task HandleUnsupportedMediaAsync(string fromPhoneNumber, string mediaType, CancellationToken ct = default)
    {
        await _sender.SendTextMessageAsync(fromPhoneNumber, mediaType switch
        {
            "audio" =>
                "🎤 Voice message mila, lekin abhi main sirf TEXT samajh sakta hoon.\n\n" +
                "Order ko likh kar bhejein, e.g.:\n\"Ayesha, 2 suit, 03001234567\"",
            "image" =>
                "📷 Yeh screenshot abhi parh nahi saka — order ki tafseel TEXT mein likh kar bhejein, e.g.:\n" +
                "\"Ayesha, 2 suit, 03001234567\"",
            _ => "Yeh file abhi main nahi samajh sakta — sirf TEXT ya screenshot bhejein, ya \"menu\" likhein."
        }, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Screen 3c: something threw while handling a message — tell the seller instead of going silent.</summary>
    public Task SendSystemErrorAsync(string fromPhoneNumber, string? issueCode = null, CancellationToken ct = default) =>
        _sender.SendTextMessageAsync(fromPhoneNumber,
            "⚠️ Kuch masla ho gaya — aapka kaam save nahi ho saka.\n" +
            "Dobara try karein, ya thodi der baad koshish karein.\n\n" +
            "Aapka message safe hai — kuch delete nahi hua." +
            (issueCode is null ? "" : $"\n\nSupport ko batana ho to yeh code bataein: {issueCode}"), ct);

    private async Task HandleIdleAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var command = CommandParser.TryParse(message);
        if (command is not null)
        {
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
            return;
        }

        await HandleFreeformMessageAsync(seller, session, ctx, message, ct);
    }

    private async Task PersistAsync(ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        session.ContextJson = ctx.ToJson();
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private Task ReplyAsync(Seller seller, string text, CancellationToken ct) =>
        _sender.SendTextMessageAsync(seller.WhatsAppPhoneNumber, text, ct);

    private static void SetState(ConversationSession session, ConversationState state) => session.State = state;

    /// <summary>Records every outbound message in message_log (saved with the engine's next SaveChanges).</summary>
    private sealed class MessageLoggingSender : IWhatsAppSender
    {
        private readonly IWhatsAppSender _inner;
        private readonly IAppDbContext _db;

        public MessageLoggingSender(IWhatsAppSender inner, IAppDbContext db)
        {
            _inner = inner;
            _db = db;
        }

        /// <summary>What this turn sent: any message at all, and whether one already carried tappable options.</summary>
        public int Sent { get; private set; }
        public bool Interactive { get; private set; }
        public void Reset() { Sent = 0; Interactive = false; }

        private void Log(string phone, string text, bool interactive = false)
        {
            Sent++;
            Interactive |= interactive;
            _db.MessageLogs.Add(new MessageLog { Phone = phone, Direction = "outbound", RawText = text });
        }

        public Task SendTextMessageAsync(string toPhoneNumber, string text, CancellationToken cancellationToken = default)
        {
            Log(toPhoneNumber, text);
            return _inner.SendTextMessageAsync(toPhoneNumber, text, cancellationToken);
        }

        public Task SendListMessageAsync(string toPhoneNumber, string bodyText, string buttonLabel, IReadOnlyList<MenuSection> sections, CancellationToken cancellationToken = default)
        {
            Log(toPhoneNumber, bodyText, interactive: true);
            return _inner.SendListMessageAsync(toPhoneNumber, bodyText, buttonLabel, sections, cancellationToken);
        }

        public Task<bool> SendFlowMessageAsync(string toPhoneNumber, string flowKind, string bodyText, string ctaLabel, CancellationToken cancellationToken = default)
        {
            Log(toPhoneNumber, $"[form {flowKind}] {bodyText}", interactive: true);
            return _inner.SendFlowMessageAsync(toPhoneNumber, flowKind, bodyText, ctaLabel, cancellationToken);
        }

        public Task SendButtonsMessageAsync(string toPhoneNumber, string bodyText, IReadOnlyList<string> buttonLabels, CancellationToken cancellationToken = default)
        {
            Log(toPhoneNumber, $"{bodyText} [{string.Join(" | ", buttonLabels)}]", interactive: true);
            return _inner.SendButtonsMessageAsync(toPhoneNumber, bodyText, buttonLabels, cancellationToken);
        }

        public Task<bool> SendTemplateMessageAsync(string toPhoneNumber, IReadOnlyList<string> bodyParameters, CancellationToken cancellationToken = default)
        {
            Log(toPhoneNumber, $"[template] {string.Join(" | ", bodyParameters)}");
            return _inner.SendTemplateMessageAsync(toPhoneNumber, bodyParameters, cancellationToken);
        }

        public Task<bool> SendDocumentAsync(string toPhoneNumber, byte[] content, string fileName, string mimeType, string? caption, CancellationToken cancellationToken = default)
        {
            Log(toPhoneNumber, $"[document {fileName}] {caption}");
            return _inner.SendDocumentAsync(toPhoneNumber, content, fileName, mimeType, caption, cancellationToken);
        }
    }
}

/// <summary>SaaS billing for the bot itself: free trial, then Basic/Pro paid to the founder's wallet (confirmed manually).</summary>
/// <summary>Feature flags (config section "Features").</summary>
public sealed class FeatureOptions
{
    public const string SectionName = "Features";

    /// <summary>Quick-action buttons (Guide / New order / Orders today) after finished replies.</summary>
    public bool ShortcutButtons { get; set; } = true;

    /// <summary>Public link to the illustrated English/Urdu guide, sent with the Guide command. Empty = no link.</summary>
    public string GuideUrl { get; set; } = "";
}

public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    public bool Enabled { get; set; } = true;
    public int TrialDays { get; set; } = 14;
    /// <summary>The first N sellers (by id) are free with no time limit and never see plans or trial messages. 0 = off.</summary>
    public int FreeSellerLimit { get; set; }
    public decimal BasicPrice { get; set; } = 300;
    public decimal ProPrice { get; set; } = 600;
    /// <summary>JazzCash/Easypaisa number sellers pay the subscription to.</summary>
    public string PaymentNumber { get; set; } = "";
    public string PaymentName { get; set; } = "Order Assistant";
}
