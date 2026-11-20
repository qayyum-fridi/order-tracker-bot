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

    /// <summary>Actions that do a job for the seller. Chat, a question back, a how-to or "not yet" is never parked.</summary>
    private static readonly HashSet<string> RequestActions = new()
    {
        TurnAction.CreateOrder, TurnAction.UpdateStatus, TurnAction.CancelOrder, TurnAction.ShowOrders, TurnAction.ShowOrder, TurnAction.ShowSummary,
        TurnAction.AddProduct, TurnAction.EditProduct, TurnAction.AddStock, TurnAction.ShowCatalog, TurnAction.AddCustomer, TurnAction.EditCustomer,
        TurnAction.EditOrder, TurnAction.ExchangeSize, TurnAction.CreateCategory, TurnAction.ShowCustomer, TurnAction.DeleteProduct, TurnAction.DeleteCustomer,
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
        return true;
    }
}
