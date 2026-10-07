using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Voice notes are read by an AI, so anything that changes money or is hard to reverse waits for a YES before it runs,
// and a multi-step sequence stops as soon as a step that must open edit mode does not.
public partial class ConversationEngine
{
    private static readonly HashSet<CommandKind> RiskyVoiceCommands = new()
    {
        CommandKind.MarkStatus, CommandKind.MarkAllPendingShipped, CommandKind.FuzzyStatusUpdate, CommandKind.CancelOrder,
        CommandKind.DeleteProduct, CommandKind.DeleteCustomer, CommandKind.EditProduct, CommandKind.PriceTiers,
        CommandKind.OrderPayment, CommandKind.OrderDeliveryCharge, CommandKind.CustomerUpdate, CommandKind.Broadcast
    };

    /// <summary>
    /// True when the steps include a risky command, or edits inside "edit order" mode (price/qty/remove/phone/...). Only for a finished
    /// seller who is idle: mid-flow answers ("yes", "1") and an edit session the seller opened themselves run straight away.
    /// </summary>
    private static bool ShouldConfirmVoiceSteps(Seller seller, IReadOnlyList<string> steps)
    {
        if (!seller.OnboardingComplete || seller.Session!.State != ConversationState.Idle) return false;

        var inEdit = false;
        foreach (var step in steps)
        {
            var command = CommandParser.TryParse(step);
            if (command?.Kind == CommandKind.EditOrder) { inEdit = true; continue; }
            if (inEdit)
            {
                if (CommandParser.TryParseOrderEdit(step, out var change) && change.Kind != "done") return true;
                continue;
            }
            if (command is not null && RiskyVoiceCommands.Contains(command.Kind)) return true;
        }
        return false;
    }

    /// <summary>Runs the steps one after another as separate typed messages; stops (and says so) if "edit order" did not open edit mode.</summary>
    private async Task RunVoiceStepsAsync(string fromPhoneNumber, IReadOnlyList<string> steps, CancellationToken ct)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            await HandleIncomingMessageAsync(fromPhoneNumber, steps[i], ct);
            if (i == steps.Count - 1 || CommandParser.TryParse(steps[i])?.Kind != CommandKind.EditOrder) continue;

            var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
            if (seller.Session!.State == ConversationState.AwaitingOrderEdit) continue;

            await _sender.SendTextMessageAsync(fromPhoneNumber,
                "⚠️ Order edit shuru nahi ho saka (order nahi mila?) — baqi steps nahi chalaye. Order number ke saath dobara try karein.", ct);
            await _db.SaveChangesAsync(ct);
            return;
        }
    }

    /// <summary>
    /// The YES/NO for steps parked by <see cref="ShouldConfirmVoiceSteps"/>. YES runs them, NO drops them, anything else drops them and is
    /// handled as a normal message (returns false). Runs before the normal pipeline so the steps are never replayed inside a half-saved turn.
    /// </summary>
    private async Task<bool> TryHandleVoiceConfirmationAsync(string fromPhoneNumber, string rawMessage, CancellationToken ct)
    {
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var session = seller.Session!;
        if (session.State != ConversationState.AwaitingVoiceConfirmation) return false;

        var ctx = SessionContextData.FromJson(session.ContextJson);
        var steps = ctx.PendingVoiceSteps;
        ctx.PendingVoiceSteps = null;
        SetState(session, ConversationState.Idle);
        await PersistAsync(session, ctx, ct);

        var message = (rawMessage ?? "").Trim();
        if (steps is { Count: > 0 } && CommandParser.IsAffirmative(message))
        {
            await RunVoiceStepsAsync(fromPhoneNumber, steps, ct);
            return true;
        }

        if (CommandParser.IsNegative(message) || message.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber, "👍 Theek hai, kuch nahi badla.", ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        return false;
    }
}
