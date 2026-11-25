using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Parking: the bot is waiting for a YES / NO or a tap, and the seller sends a voice note asking for something new. The new request is kept in
// ModelQueue and the open question is asked again; once it is answered the new request runs (or asks its guided question). It is not run first:
// it would overwrite the open question's saved details (one session store), so a later YES could apply to the wrong thing.
public partial class ConversationEngine
{
    /// <summary>Questions whose answer is a YES / NO or a tap: a long sentence sent while one is open is a new request, not the answer.</summary>
    private static readonly HashSet<ConversationState> ParkableStates = new()
    {
        ConversationState.AwaitingModelStatusConfirmation, ConversationState.AwaitingOrderConfirmation, ConversationState.AwaitingMultiOrderConfirmation,
        ConversationState.AwaitingClarificationChoice, ConversationState.AwaitingOrderGroupingChoice, ConversationState.AwaitingRuntimeFilterChoice,
        ConversationState.AwaitingReceiptOrderChoice, ConversationState.AwaitingCancelConfirmation, ConversationState.AwaitingBulkStatusConfirmation,
        ConversationState.AwaitingDuplicateOrderConfirmation, ConversationState.AwaitingCodCollectedConfirmation,
        ConversationState.AwaitingDeleteCustomerConfirmation, ConversationState.AwaitingVoiceConfirmation,
    };

    /// <summary>
    /// Typed parking is narrower than voice: only pure YES / NO steps. An order draft or a pick list also takes typed corrections and
    /// answers by meaning ("phone 0300...", "doosra wala Sara ka"), which must reach their own handler.
    /// </summary>
    private static readonly HashSet<ConversationState> TypedParkStates = new()
    {
        ConversationState.AwaitingModelStatusConfirmation, ConversationState.AwaitingCancelConfirmation, ConversationState.AwaitingBulkStatusConfirmation,
        ConversationState.AwaitingDuplicateOrderConfirmation, ConversationState.AwaitingCodCollectedConfirmation, ConversationState.AwaitingDeleteCustomerConfirmation,
    };

    /// <summary>
    /// A typed sentence at a YES / NO step that asks for something new (not a yes / no word, not a typed command): it is kept and the question asked
    /// again, instead of being read as NO and lost. True when it was parked.
    /// </summary>
    private async Task<bool> TryParkTypedAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct) =>
        seller.OnboardingComplete && TypedParkStates.Contains(session.State) && !LooksLikeAnswer(message) && CommandParser.TryParse(message) is null
        && await TryModelFirstAsync(seller, session, ctx, message, ct, parkOnly: true);

    /// <summary>Actions that do a job for the seller. Chat, a question back, a how-to or "not yet" is never parked.</summary>
    private static readonly HashSet<string> RequestActions = new()
    {
        TurnAction.CreateOrder, TurnAction.UpdateStatus, TurnAction.CancelOrder, TurnAction.ShowOrders, TurnAction.ShowOrder, TurnAction.ShowSummary,
        TurnAction.AddProduct, TurnAction.EditProduct, TurnAction.AddStock, TurnAction.ShowCatalog, TurnAction.AddCustomer, TurnAction.EditCustomer,
        TurnAction.EditOrder, TurnAction.ExchangeSize, TurnAction.CreateCategory, TurnAction.ShowCustomer, TurnAction.DeleteProduct, TurnAction.DeleteCustomer,
        TurnAction.RestoreProduct, TurnAction.RestoreCustomer,
        TurnAction.RecordPayment, TurnAction.CodPending, TurnAction.RecordPayout, TurnAction.SetTracking, TurnAction.RecordExpense, TurnAction.MonthlyExpense,
    };

    private static readonly Regex JustANumber = new(@"^\s*#?\d{1,3}\s*$", RegexOptions.Compiled);

    /// <summary>A short reply that answers the open question ("haan", "nahi", "2", "doosra wala"): never parked.</summary>
    internal static bool LooksLikeAnswer(string message)
    {
        var text = message.Trim().Trim('.', '!', '?', '،', '۔');
        return CommandParser.IsAffirmative(text) || CommandParser.IsNegative(text) || CommandParser.IsAcknowledgement(text) || JustANumber.IsMatch(text)
               || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2;
    }

    /// <summary>True when the request was parked (and the open question asked again).</summary>
    private async Task<bool> TryParkAsync(Seller seller, ConversationSession session, SessionContextData ctx, IReadOnlyList<TurnAction> actions, string message,
        Func<Task>? beforeRun, CancellationToken ct)
    {
        if (!seller.OnboardingComplete || !ParkableStates.Contains(session.State) || LooksLikeAnswer(message)
            || !actions.Any(a => RequestActions.Contains(a.Action)))
            return false;

        if (beforeRun is not null) await beforeRun();
        foreach (var a in actions.Where(a => RequestActions.Contains(a.Action)))
            ctx.ModelQueue.Add(new QueuedModelTurn(Plain(a), message));
        await ReplyAsync(seller, MessageTemplates.Get("park.001", GuidanceFor(session.State, ctx, seller)), ct);
        // The seller may have scrolled past the open question: ask it again with its own options as taps.
        if (session.State == ConversationState.AwaitingClarificationChoice && ctx.ClarificationOptions is { Count: > 0 } open)
            await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("park.002"), open.Select(o => new ChoiceOption(o, o)).ToList(), ct);
        return true;
    }

    /// <summary>A 1 / 2 pick question older than this is dropped: the seller has moved on, and every new request would otherwise be parked behind it.</summary>
    private static readonly TimeSpan PickQuestionExpiry = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Drops an open pick question (<see cref="ConversationState.AwaitingClarificationChoice"/>) that was asked more than <see cref="PickQuestionExpiry"/> ago.
    /// Requests parked behind it stay in <see cref="SessionContextData.ModelQueue"/> and run at the end of this message. True when it was dropped.
    /// </summary>
    private async Task<bool> DropExpiredPickQuestionAsync(Seller seller, ConversationSession session, SessionContextData ctx, CancellationToken ct)
    {
        if (session.State != ConversationState.AwaitingClarificationChoice || ctx.PickAskedAt is not { } asked
            || _clock.GetUtcNow().UtcDateTime - asked <= PickQuestionExpiry)
            return false;
        ctx.ClarificationOptions = null;
        ctx.ClarifiedText = null;
        ctx.PickAskedAt = null;
        SetState(session, ConversationState.Idle);
        if (ctx.ModelQueue.Count > 0) await ReplyAsync(seller, MessageTemplates.Get("park.003"), ct);
        return true;
    }
}
