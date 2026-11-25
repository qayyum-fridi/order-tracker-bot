using Microsoft.Extensions.Logging;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Scored choice: the understanding call also says how sure it is (0 to 10) and what else the message could mean. When it is not sure, or a second
// action scores close to the first, the bot does not run a guess: it asks which one is meant, with taps in plain words and a "Kuch aur" way out.
// A tap runs the model again with that action confirmed (so its fields are filled from the same message), then the action runs through the usual
// handlers, including their own YES for anything that changes money or data. The taps use the pick-question state, so re-showing them while another
// request is parked, and dropping them after 10 minutes, already work (ConversationEngine.Parking.cs).
public partial class ConversationEngine
{
    /// <summary>A score at or above this, with the next action well behind, runs without asking.</summary>
    private const int ActionRunScore = 8;
    /// <summary>The first action must lead the next one by at least this many points to run without asking.</summary>
    private const int ActionRunGap = 4;
    /// <summary>An alternative below this score is not worth a tap.</summary>
    private const int ActionMinScore = 3;
    /// <summary>At most this many actions are offered (plus "Kuch aur"), so they stay one-tap buttons.</summary>
    private const int ActionChoiceLimit = 2;
    internal const string ActionNoneLabel = "Kuch aur";

    /// <summary>
    /// The tap for each action, in plain Roman Urdu: at most 20 characters (a button title), never a typed command. The engine matches on these, so they stay in code.
    /// Chat, a question back and "not in scope" have no tap: they are the "Kuch aur" way out.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ActionLabels = new Dictionary<string, string>
    {
        [TurnAction.CreateOrder] = "Order likhna",
        [TurnAction.UpdateStatus] = "Order ki halat",
        [TurnAction.CancelOrder] = "Order radd karna",
        [TurnAction.ShowOrders] = "Orders dekhna",
        [TurnAction.ShowOrder] = "Ek order dekhna",
        [TurnAction.ShowSummary] = "Hisaab dekhna",
        [TurnAction.AddProduct] = "Product jodna",
        [TurnAction.EditProduct] = "Qeemat badalna",
        [TurnAction.AddStock] = "Stock barhana",
        [TurnAction.ShowCatalog] = "Products dekhna",
        [TurnAction.AddCustomer] = "Customer jodna",
        [TurnAction.EditCustomer] = "Customer badalna",
        [TurnAction.EditOrder] = "Order badalna",
        [TurnAction.ExchangeSize] = "Size badalna",
        [TurnAction.CreateCategory] = "Category banana",
        [TurnAction.ShowCustomer] = "Customer dekhna",
        [TurnAction.DeleteProduct] = "Product hatana",
        [TurnAction.DeleteCustomer] = "Customer hatana",
        [TurnAction.RestoreProduct] = "Product wapas",
        [TurnAction.RestoreCustomer] = "Customer wapas",
        [TurnAction.RecordPayment] = "Paisa mila",
        [TurnAction.CodPending] = "COD baqi dekhna",
        [TurnAction.RecordPayout] = "Courier ka paisa",
        [TurnAction.SetTracking] = "Tracking number",
        [TurnAction.RecordExpense] = "Kharcha likhna",
        [TurnAction.MonthlyExpense] = "Mahana kharcha",
        [TurnAction.HowTo] = "Tarika janna",
    };

    /// <summary>True when the score is missing-free and low, or another action is within <see cref="ActionRunGap"/> points of it.</summary>
    internal static bool NeedsActionChoice(TurnAction action) =>
        action.Score is { } score && (score < ActionRunScore || action.Alternatives.Any(a => a.Score > score - ActionRunGap));

    /// <summary>True when the "which one?" taps were sent. False = the action is trusted (or nothing worth offering): the caller carries on as before.</summary>
    private async Task<bool> TryAskWhichActionAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, string message,
        bool fromVoice, Func<Task>? beforeAsk, CancellationToken ct)
    {
        if (!NeedsActionChoice(action)) return false;
        var options = new[] { new ActionGuess(action.Action, action.Score!.Value) }.Concat(action.Alternatives)
            .Where(g => ActionLabels.ContainsKey(g.Action) && (g.Action == action.Action || g.Score >= ActionMinScore))
            .OrderByDescending(g => g.Score).Take(ActionChoiceLimit)
            .Select(g => new ActionChoiceOption { Action = g.Action, Label = ActionLabels[g.Action] }).ToList();
        if (options.Count == 0) return false;
        options.Add(new ActionChoiceOption { Action = "none", Label = ActionNoneLabel });

        if (beforeAsk is not null) await beforeAsk();
        ctx.ActionChoice = new ActionChoiceData { Message = message, FromVoice = fromVoice, Options = options };
        ctx.ClarificationOptions = options.Select(o => o.Label).ToList();
        ctx.ClarifiedText = null;
        ctx.GuidedTurn = null;
        SetState(session, ConversationState.AwaitingClarificationChoice);
        // Scores only, never the message text (it holds names and amounts).
        _logger?.LogInformation("ModelFirst: not sure (score {Score}, first {Action}), asked which of {Count} actions was meant", action.Score, action.Action, options.Count - 1);
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, MessageTemplates.Get("model-first.pick"),
            ctx.ClarificationOptions.Select(l => new ChoiceOption(l, l)).ToList(), ct);
        return true;
    }

    /// <summary>The seller's answer to the "which one?" taps. True when this was such a question (it is answered here).</summary>
    private async Task<bool> TryHandleActionChoiceAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.ActionChoice is not { } choice) return false;
        var text = message.Trim();
        var index = int.TryParse(text, out var number) && number >= 1 && number <= choice.Options.Count
            ? number - 1
            : choice.Options.FindIndex(o => string.Equals(o.Label, text, StringComparison.OrdinalIgnoreCase));
        ctx.ActionChoice = null;
        ctx.ClarificationOptions = null;
        ctx.ClarifiedText = null;
        SetState(session, ConversationState.Idle);

        if (index < 0)
        {
            // Not a tap: the seller moved on. A typed command runs; anything else is a new message.
            if (CommandParser.TryParse(message) is { } command) await ExecuteCommandAsync(seller, session, ctx, command, ct);
            else await HandleFreeformMessageAsync(seller, session, ctx, message, ct);
            return true;
        }

        var profile = Industry(seller);
        var picked = choice.Options[index];
        if (picked.Action == "none")
        {
            await ReplyAsync(seller, MessageTemplates.Get("model-first.unclear", profile.MainLine, profile.OrderExample), ct);
            return true;
        }

        var catalog = await LoadCatalogAsync(seller, ct);
        var context = await BuildTurnContextAsync(seller, catalog, choice.Message, choice.FromVoice, picked.Action, ct);
        var confirmed = _turnInterpreter is null ? null : await _turnInterpreter.UnderstandAsync(context, choice.Message, null, ct);
        if (confirmed is null || confirmed.Action != picked.Action)
        {
            await ReplyAsync(seller, MessageTemplates.Get("model-first.unclear", profile.MainLine, profile.OrderExample), ct);
            return true;
        }
        await RunOrGuideAsync(seller, session, ctx, Plain(confirmed), catalog, choice.Message, ct, false, guide: true);
        return true;
    }
}
