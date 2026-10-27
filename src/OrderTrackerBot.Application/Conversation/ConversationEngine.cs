using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

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
    private readonly Microsoft.Extensions.Logging.ILogger<ConversationEngine>? _logger;
    private static readonly TimeSpan PendingPriceWindow = TimeSpan.FromMinutes(10);
    private static readonly System.Text.RegularExpressions.Regex BareAmount = new(@"^(?:rs\.?\s*|pkr\s*)?(?<n>\d[\d,]*(?:\.\d+)?)(?:\s*(?:rs|rupay|rupees|روپے))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private DateTime? _sentAtUtc;
    private List<string>? _nextActions;

    /// <summary>The time to stamp on a record made by this turn: the real send time of a delayed message (within 3 days, never in the future), else now.</summary>
    private DateTime RecordTimeUtc()
    {
        var now = DateTime.UtcNow;
        return _sentAtUtc is { } sent && sent <= now && now - sent <= TimeSpan.FromDays(3) ? sent : now;
    }
    private readonly INewSellerNotifier? _newSellers;
    private readonly MediaRateLimiter? _mediaLimiter;
    private readonly TimeProvider _clock;
    private readonly IAudioTranscriber? _transcriber;
    private readonly IMerchantEmailSender? _email;
    private readonly MessageLoggingSender _turn;
    private Seller? _turnSeller;
    /// <summary>Set when this turn put an open order draft on hold to answer a side question (see route "other" in ConversationEngine.OrderFlow.cs).</summary>
    private bool _draftPutOnHold;
    private SessionContextData? _turnCtx;

    public ConversationEngine(IAppDbContext db, IAiOrderAssistant ai, IWhatsAppSender sender, IFounderAlertNotifier founderAlerts,
        BillingOptions? billing = null, IWhatsAppMediaClient? media = null, IInstagramClient? instagram = null, ICatalogSheetImporter? catalogSheets = null,
        IReceiptPdfGenerator? receiptPdf = null, IExportFileWriter? exportWriter = null, FeatureOptions? features = null,
        IIssueReporter? issues = null, IAudioTranscriber? transcriber = null, IImportFileReader? importReader = null,
        INewSellerNotifier? newSellers = null, MediaRateLimiter? mediaLimiter = null, TimeProvider? clock = null,
        IMerchantEmailSender? emailSender = null, Microsoft.Extensions.Logging.ILogger<ConversationEngine>? logger = null, BuildInfo? build = null,
        DashboardOptions? dashboard = null)
    {
        _build = build ?? new BuildInfo();
        _dashboard = dashboard ?? new DashboardOptions();
        _logger = logger;
        _email = emailSender;
        _clock = clock ?? TimeProvider.System;
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
        _features = features ?? new FeatureOptions { ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false, SetupNudges = false, IndustryQuestion = false };
    }

    private static readonly ConversationState[] OnboardingStates =
    {
        ConversationState.OnboardingBusinessName, ConversationState.OnboardingLanguage, ConversationState.OnboardingOptionalDetails,
        ConversationState.OnboardingCatalogSize, ConversationState.OnboardingAddProduct, ConversationState.OnboardingStartChoice,
        ConversationState.OnboardingIndustry
    };

    /// <summary>
    /// <paramref name="sentAtUtc"/> is when the message was really sent. A seller whose phone was offline sends entries that arrive much later;
    /// records created from them (orders, expenses) keep the send time, not the arrival time.
    /// </summary>
    public async Task HandleIncomingMessageAsync(string fromPhoneNumber, string rawMessage, CancellationToken ct = default, DateTime? sentAtUtc = null)
    {
        _sentAtUtc = sentAtUtc;
        _nextActions = null;
        _turn.Reset();
        _turnSeller = null;
        _turnCtx = null;
        _stockWarnings.Clear();
        if (await TryHandleVoiceConfirmationAsync(fromPhoneNumber, rawMessage, ct)) return;
        var answeredDraft = IsOrderDraftState((await LoadOrCreateSellerAsync(fromPhoneNumber, ct)).Session!.State);
        _draftPutOnHold = false;
        await HandleIncomingCoreAsync(fromPhoneNumber, rawMessage, ct);
        if (_draftPutOnHold) await HoldParkedVoiceStepsAsync(fromPhoneNumber, ct);
        await ResumeVoiceContinueAsync(fromPhoneNumber, answeredDraft, ct);
        await FlushStockWarningsAsync(fromPhoneNumber, ct);
        await TrySendShortcutBarAsync(fromPhoneNumber, ct);
    }

    /// <summary>One log line for a message the bot did not understand: <c>grep NotUnderstood</c> in the server log lists the real phrasings to teach it.</summary>
    private void LogNotUnderstood(string path, ConversationState state, string? text)
    {
        if (!_features.TurnLog || string.IsNullOrWhiteSpace(text)) return;
        _logger?.LogInformation("NotUnderstood path={Path} state={State} text={Text}", path, state, text.Length > 300 ? text[..300] : text);
    }

    private static readonly TimeSpan DraftExpiry = TimeSpan.FromMinutes(20);

    private async Task HandleIncomingCoreAsync(string fromPhoneNumber, string rawMessage, CancellationToken ct)
    {
        // A typed backslash-n (copied from code or a template) is meant as a line break.
        var message = TextHygiene.StripInvisible(rawMessage ?? string.Empty).Replace("\\n", "\n").Trim();
        if (message.Length == 0) return;

        _db.MessageLogs.Add(new MessageLog { Phone = fromPhoneNumber, Direction = "inbound", RawText = message });
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        if (seller.Status != SellerStatus.Active)
        {
            await ReplyAsync(seller, seller.Status == SellerStatus.Cancelled
                ? MessageTemplates.Get("engine.004")
                : MessageTemplates.Get("engine.005"), ct);
            return;
        }
        await SendPendingNoticesAsync(seller, ct);
        // Every text counts against the seller's hourly text cap and USD budget, before any AI call can be made for it.
        if (!TryTakeUsage(seller, fromPhoneNumber, MediaKind.Text, out var textDecision, out var textWait))
        {
            await ReplyAsync(seller, UsageLimitText(seller, MediaKind.Text, textDecision, textWait), ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
        message = _translator.RestoreButtonLabel(message);

        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);
        _turnSeller = seller;
        _turnCtx = ctx;

        // A product line waiting for YES: saved on YES, dropped on NO (see ConversationEngine.ProductConfirm.cs).
        if (await TryHandlePendingProductAsync(seller, ctx, message, ct))
        {
            await PersistAsync(session, ctx, ct); // the saved products and the cleared pending state must reach the database
            return;
        }

        // A tap on a product button: an answer to the unknown-product question ("2 <product>" / "1"), or one of the customer's usual items after "order for Sara"
        // (read as her items). With no such question waiting it is a stale tap.
        if (ctx.ProductSuggestions is { Count: > 0 } offered
            && (message.Trim() == NewProductButtonTitle || offered.Any(o => o.Title == message.Trim())))
        {
            var tappedTitle = message.Trim();
            if (session.State == ConversationState.AwaitingOrderMissingFields && ctx.PendingMissingField == "ProductChoice")
                message = tappedTitle == NewProductButtonTitle ? "1" : "2 " + offered.First(o => o.Title == tappedTitle).Label;
            else if (session.State == ConversationState.Idle && ctx.OrderForCustomer is not null && tappedTitle != NewProductButtonTitle)
                message = offered.First(o => o.Title == tappedTitle).Label;
            else
            {
                await ReplyAsync(seller, MessageTemplates.Get("engine.001"), ct);
                await PersistAsync(session, ctx, ct);
                return;
            }
        }

        // A tap on the draft's Save / Edit / Cancel buttons arrives as its label: it acts as typed yes / edit / cancel, and is told apart from a stale tap.
        if (Array.IndexOf(ConfirmButtonLabels, message.Trim()) is var tapped and >= 0)
        {
            if (session.State == ConversationState.AwaitingOrderConfirmation) message = ConfirmButtonCommands[tapped];
            else
            {
                await ReplyAsync(seller, MessageTemplates.Get("engine.002"), ct);
                await PersistAsync(session, ctx, ct);
                return;
            }
        }

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
            await StartResetAsync(seller, session, ctx, ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        // "login" and "dashboard logout" work at every step, including onboarding: the dashboard only needs the WhatsApp number.
        var dashboardKind = CommandParser.TryParse(message)?.Kind;
        if (dashboardKind is CommandKind.ClientLogin or CommandKind.ClientLogout)
        {
            if (dashboardKind == CommandKind.ClientLogin) await HandleClientLoginAsync(seller, ct);
            else await HandleClientLogoutAsync(seller, ct);
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

        // An order draft left waiting for a long time is dropped: a phone or name sent hours later must not be taken as its answer.
        if (session.State is ConversationState.AwaitingOrderConfirmation or ConversationState.AwaitingOrderMissingFields
                or ConversationState.AwaitingOrderGroupingChoice or ConversationState.AwaitingMultiOrderConfirmation
            && ctx.PendingOrder is not null && DateTime.UtcNow - session.UpdatedAt > DraftExpiry)
        {
            ctx.PendingOrder = null;
            ctx.QueuedOrders = null;
            ctx.PendingMissingField = null;
            ctx.PendingNewProductName = null;
            SetState(session, ConversationState.Idle);
            await ReplyAsync(seller, MessageTemplates.Get("order-flow.draftexpired"), ct);
        }

        // A draft state with no draft behind it (left by an earlier turn) would answer every message as if an order were waiting.
        if (session.State is ConversationState.AwaitingOrderConfirmation or ConversationState.AwaitingOrderMissingFields && ctx.PendingOrder is null)
            SetState(session, ConversationState.Idle);

        if (session.State is ConversationState.AwaitingOrderConfirmation or ConversationState.AwaitingOrderMissingFields
                or ConversationState.AwaitingOrderGroupingChoice or ConversationState.AwaitingMultiOrderConfirmation
            && await TryLeaveOrderDraftAsync(seller, session, ctx, message, ct))
        {
            await PersistAsync(session, ctx, ct);
            return;
        }

        if (await TryRouteMidFlowReplyAsync(seller, session, ctx, message, ct))
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
                await HandleResetConfirmationAsync(seller, session, ctx, message, ct);
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
            case ConversationState.AwaitingReceiptDesign:
                await HandleReceiptDesignAsync(seller, session, ctx, message, ct);
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
            _ => MessageTemplates.Get("engine.006")
        }, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Screen 3c: something threw while handling a message — tell the seller instead of going silent.</summary>
    /// <summary>Sent while this seller's previous message is still being handled, so the reply is not mistaken for a lost one.</summary>
    public async Task SendWaitingNoticeAsync(string fromPhoneNumber, CancellationToken ct = default)
    {
        await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get("engine.004"), ct);
    }

    public Task SendSystemErrorAsync(string fromPhoneNumber, string? issueCode = null, CancellationToken ct = default) =>
        _sender.SendTextMessageAsync(fromPhoneNumber,
            MessageTemplates.Get("engine.003", (issueCode is null ? "" : $"\n\nSupport ko batana ho to yeh code bataein: {issueCode}")), ct);

    private async Task HandleIdleAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        // Waiting for new products' prices, but the seller clearly asks for something else ("Kashif ka naya order likho"): park the price
        // question for this turn so the message is read on its own, and put it back unless this turn started its own price question.
        // An old price question is dropped first: a number sent much later (a stock quantity, say) must not be read as that product's price.
        if (ctx.PendingPriceSetAt is { } askedAt && _clock.GetUtcNow().UtcDateTime - askedAt > PendingPriceWindow)
        {
            ctx.PendingPriceProducts = null;
            ctx.PendingPriceSetAt = null;
        }
        var parkedPrices = ctx.PendingPriceProducts is { Count: > 0 } && CommonWordPathRouter.ShouldLeavePriceWait(message) ? ctx.PendingPriceProducts : null;
        if (parkedPrices is not null) ctx.PendingPriceProducts = null;
        try
        {
            await HandleIdleCoreAsync(seller, session, ctx, message, ct);
        }
        finally
        {
            if (parkedPrices is not null) ctx.PendingPriceProducts ??= parkedPrices;
        }
    }

    private async Task HandleIdleCoreAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        var decision = TurnGate.DecideTyped(message);
        if (decision.Kind == DecisionKind.Reject)
        {
            await ReplyAsync(seller, decision.Message!, ct);
            return;
        }

        if (await TryHandleOrderForCustomerItemsAsync(seller, session, ctx, message, ct)) return;
        if (await TryHandleItemsOnlyAsync(seller, session, ctx, message, ct)) return;

        // Waiting for the price of ONE new product and the seller sends just an amount ("9000", "Rs. 9,000"): that is its price. Plain code, no AI guess.
        if (ctx.PendingPriceProducts is { Count: 1 } waiting && BareAmount.Match(message.Trim()) is { Success: true } bare
            && decimal.TryParse(bare.Groups["n"].Value.Replace(",", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount > 0)
        {
            await HandleNewProductsAsync(seller, ctx, new[] { new AiNewProduct { Name = waiting[0], Price = amount } }, message, ct);
            return;
        }

        // A scanner types a product's barcode as a message: a code that is some product's barcode goes to the scan cart.
        if (await TryHandleScannedCodeAsync(seller, ctx, message, ct)) return;

        var command = CommandParser.TryParse(message);
        // A sentence the rules do not know but THIS seller once taught (by picking the right option) is understood without asking again.
        if (command is null && LearnedCommandFor(ctx, message) is { } learnedCommand)
        {
            _logger?.LogInformation("Learned phrase used: {Kind}", learnedCommand.Kind);
            await ExecuteCommandAsync(seller, session, ctx, learnedCommand, ct);
            return;
        }
        // Not the exact sentence but a close one ("aaj ki sales dikha do" after "aaj ki sale dikhao"): the AI says whether it is the same request.
        if (command is null && SimilarLearnedPhrase(ctx, message) is { } similar
            && await _ai.ConfirmSamePhraseAsync(similar.Phrase, message, ct) == true
            && CommandParser.TryParse(similar.Command) is { } similarCommand && !ActionRisk.RiskyCommands.Contains(similarCommand.Kind))
        {
            _logger?.LogInformation("Learned phrase used (similar): {Kind}", similarCommand.Kind);
            await ExecuteCommandAsync(seller, session, ctx, similarCommand, ct);
            return;
        }
        // Level 3 prototype: no shared words at all, but the same MEANING as something this seller taught.
        if (command is null && await SemanticLearnedCandidateAsync(ctx, message, ct) is { } semantic
            && await _ai.ConfirmSamePhraseAsync(semantic.Phrase, message, ct) == true
            && CommandParser.TryParse(semantic.Command) is { } semanticCommand && !ActionRisk.RiskyCommands.Contains(semanticCommand.Kind))
        {
            _logger?.LogInformation("Learned phrase used (semantic): {Kind} score {Score:0.00}", semanticCommand.Kind, semantic.Score);
            await ExecuteCommandAsync(seller, session, ctx, semanticCommand, ct);
            return;
        }
        if (command is not null)
        {
            // A loose rule (keywords in any order) may have matched a sentence that is really something else, e.g. an order: ask the AI before running it.
            if (await LooseRuleMatchIsWrongAsync(command, message, ct))
            {
                LogNotUnderstood("rule-overruled-" + command.Kind, session.State, message);
                await HandleFreeformMessageAsync(seller, session, ctx, message, ct);
                return;
            }
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
            return;
        }

        await HandleFreeformMessageAsync(seller, session, ctx, message, ct);
    }

    /// <summary>The text of a loose-rule match that was RUN (confirmed by the AI, unknown, or too short to check), for reviewing false positives. Same switch as <c>NotUnderstood</c>.</summary>
    private void LogRuleText(string tag, CommandKind kind, bool? fits, string text)
    {
        if (!_features.TurnLog) return;
        _logger?.LogInformation("{Tag} kind={Kind} fits={Fits} text={Text}", tag, kind, fits?.ToString() ?? "n/a", text.Length > 300 ? text[..300] : text);
    }

    // Commands the parser finds by LOOSE rules (keyword combinations, fuzzy names). Exact commands ("orders today", "mark 3 shipped") are never second-guessed.
    /// <summary>A loose-rule match on a message of at least this many words is checked by the AI first (was 5; 3 so short ambiguous phrases like "customer status bhejo" are covered).</summary>
    private const int LooseRuleMinWords = 3;

    private static readonly HashSet<CommandKind> LooseRuleKinds = new()
    {
        CommandKind.Insight, CommandKind.FuzzyStatusUpdate, CommandKind.CustomerOrderLookup, CommandKind.ProductReport, CommandKind.CustomerSearch, CommandKind.OrderSearch, CommandKind.HowTo
    };

    private async Task<bool> LooseRuleMatchIsWrongAsync(ParsedCommand command, string message, CancellationToken ct)
    {
        if (!LooseRuleKinds.Contains(command.Kind)) return false;
        if (message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < LooseRuleMinWords)
        {
            // Too short for the AI check (fewer than LooseRuleMinWords words): only recorded (with the text, Features:TurnLog) so the owner can see which short phrases the loose rules take.
            LogRuleText("RuleShort", command.Kind, null, message);
            return false;
        }
        var fits = await _ai.ConfirmCommandMatchAsync(message, command.Kind.ToString(), ct);
        _logger?.LogInformation("RuleCheck kind={Kind} fits={Fits}", command.Kind, fits?.ToString() ?? "unknown");
        if (fits != false) LogRuleText("RuleConfirmed", command.Kind, fits, message);
        return fits == false;
    }

    private async Task PersistAsync(ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        // A draft's clock starts when it is first persisted, and stops when the draft is gone.
        if (ctx.PendingOrder is null) ctx.PendingOrderAskedAt = null;
        else ctx.PendingOrderAskedAt ??= _clock.GetUtcNow().UtcDateTime;
        session.ContextJson = ctx.ToJson();
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private readonly BuildInfo _build;
    private readonly DashboardOptions _dashboard;

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

        public Task<bool> SendTemplateMessageAsync(string toPhoneNumber, IReadOnlyList<string> bodyParameters, string templateKey = WhatsAppTemplateKeys.Broadcast, CancellationToken cancellationToken = default)
        {
            Log(toPhoneNumber, $"[template {templateKey}] {string.Join(" | ", bodyParameters)}");
            return _inner.SendTemplateMessageAsync(toPhoneNumber, bodyParameters, templateKey, cancellationToken);
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

    /// <summary>Quick-action buttons (Guide / New order / Menu) after finished replies.</summary>
    public bool ShortcutButtons { get; set; } = true;

    /// <summary>Save / Edit / Cancel as tap buttons under an order draft (typed YES / EDIT / cancel keep working).</summary>
    public bool ConfirmButtons { get; set; } = true;

    /// <summary>Did-you-mean taps (nearest catalog products) when an order names a product that is not in the catalog; the typed hint is used when off.</summary>
    public bool SuggestionButtons { get; set; } = true;

    /// <summary>Once-a-day reminder after a saved order when products lack a cost price (profit reports need it); <c>data check</c> works either way.</summary>
    public bool SetupNudges { get; set; }

    /// <summary>Setup asks the kind of shop right after the shop name (taps); off = the old flow without that question (unit tests).</summary>
    public bool IndustryQuestion { get; set; }

    /// <summary>A product line is shown back (name, colour, price, stock) and saved only after YES; off = saved straight away (unit tests).</summary>
    public bool ProductConfirmation { get; set; }

    /// <summary>Instagram connect, comment leads and the Meta webhooks. Off = "connect instagram" says it is not active, comments are ignored and the Instagram endpoints answer 404.</summary>
    public bool Instagram { get; set; }

    /// <summary>The "📥 Purana data" button on the first setup screen. Off = the button is hidden (MVP); typing "purana data" / "import" still works.</summary>
    public bool OldDataImport { get; set; } = true;

    /// <summary>MVP cut: the features that are off for now (discounts, loyalty, payments, payouts, drawer, labels, Instagram, import, walk-in, forms, scan) answer one "not available yet" reply when typed, not only hidden from menus. Off in code so existing tests keep their behaviour; on in appsettings.</summary>
    public bool MvpOnly { get; set; }

    /// <summary>Menu: discount rows (menu discounts, discount list, add discount, discount performance). Off = hidden from the menu; the typed commands still work.</summary>
    public bool Discounts { get; set; } = false;

    /// <summary>Menu: loyalty rows (add loyalty rule, loyal customers). Off = hidden from the menu; the typed commands still work.</summary>
    public bool Loyalty { get; set; } = false;

    /// <summary>Menu: payment rows (Payments category, payment link, payment methods, courier payouts). Off = hidden from the menu; the typed commands still work. Unpaid / COD lists stay.</summary>
    public bool Payments { get; set; } = false;

    /// <summary>Menu: courier payout rows (payout pending). Off = hidden from the menu; the typed commands still work.</summary>
    public bool Payouts { get; set; } = false;

    /// <summary>Menu: cash drawer rows (drawer). Off = hidden from the menu; the typed commands still work.</summary>
    public bool Drawer { get; set; } = false;

    /// <summary>Menu: label rows (shipping label, business label). Off = hidden from the menu; the typed commands still work.</summary>
    public bool Labels { get; set; } = false;

    /// <summary>A short "listening" message the moment a voice note arrives, so a long transcription is not silent. Off = no extra message (unit tests).</summary>
    public bool VoiceAck { get; set; }

    /// <summary>Log every message the bot could NOT understand (path, state and the seller's text) so the owner can see which real phrasings fail. The log holds seller text: keep it off if that is not acceptable. Off = nothing logged (unit tests).</summary>
    public bool TurnLog { get; set; }

    /// <summary>Prototype: a sentence that means the same as a taught phrase (found with embeddings, confirmed by the AI) runs the taught command. Off = nothing changes (unit tests).</summary>
    public bool SemanticLearning { get; set; }

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
