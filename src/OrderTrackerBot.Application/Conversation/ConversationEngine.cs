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
    private readonly IFounderAlertNotifier _founderAlerts;

    public ConversationEngine(IAppDbContext db, IAiOrderAssistant ai, IWhatsAppSender sender, IFounderAlertNotifier founderAlerts)
    {
        _db = db;
        _ai = ai;
        _sender = sender;
        _founderAlerts = founderAlerts;
    }

    public async Task HandleIncomingMessageAsync(string fromPhoneNumber, string rawMessage, CancellationToken ct = default)
    {
        var message = (rawMessage ?? string.Empty).Trim();
        if (message.Length == 0) return;

        var seller = await _db.Sellers
            .Include(s => s.Session)
            .FirstOrDefaultAsync(s => s.WhatsAppPhoneNumber == fromPhoneNumber, ct);

        if (seller is null)
        {
            seller = new Seller { WhatsAppPhoneNumber = fromPhoneNumber };
            seller.Session = new ConversationSession { State = ConversationState.Idle };
            _db.Sellers.Add(seller);
            await _db.SaveChangesAsync(ct);
        }

        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);

        if (session.State != ConversationState.AwaitingResetConfirmation
            && CommandParser.TryParse(message)?.Kind == CommandKind.ResetAccount)
        {
            await StartResetAsync(seller, session, ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        if (!seller.OnboardingComplete || session.State is ConversationState.OnboardingBusinessName
            or ConversationState.OnboardingLanguage or ConversationState.OnboardingCatalogSize or ConversationState.OnboardingAddProduct)
        {
            await HandleOnboardingAsync(seller, session, ctx, message, ct);
            await PersistAsync(session, ctx, ct);
            return;
        }

        if (session.State is ConversationState.AwaitingOrderConfirmation or ConversationState.AwaitingOrderMissingFields
                or ConversationState.AwaitingOrderGroupingChoice
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
            case ConversationState.AwaitingBroadcastAudienceChoice:
                await HandleBroadcastAudienceChoiceAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingDiscountDetails:
                await HandleDiscountDetailsAsync(seller, session, ctx, message, ct);
                break;
            case ConversationState.AwaitingResetConfirmation:
                await HandleResetConfirmationAsync(seller, session, message, ct);
                break;
            default:
                await HandleIdleAsync(seller, session, ctx, message, ct);
                break;
        }

        await PersistAsync(session, ctx, ct);
    }

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
}
