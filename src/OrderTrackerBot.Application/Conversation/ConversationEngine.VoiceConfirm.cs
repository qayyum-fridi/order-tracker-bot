using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Voice notes are read by an AI, so anything that changes money or is hard to reverse waits for a YES before it runs,
// and a multi-step sequence stops as soon as a step that must open edit mode does not.
public partial class ConversationEngine
{
    /// <summary>
    /// Splits the note into steps that run now and steps that change money or an order and wait for a YES. An edit chain ("edit order N",
    /// its edit lines, "done") waits as one piece. Only for a finished seller who is idle: mid-flow answers ("yes", "1") and an edit session
    /// the seller opened themselves run straight away.
    /// </summary>
    private static (List<string> Safe, List<string> Risky) SplitVoiceSteps(Seller seller, IReadOnlyList<string> steps)
    {
        var safe = new List<string>();
        var risky = new List<string>();
        if (!seller.OnboardingComplete || seller.Session!.State != ConversationState.Idle)
        {
            safe.AddRange(steps);
            return (safe, risky);
        }

        var inEdit = false;
        foreach (var step in steps)
        {
            var command = CommandParser.TryParse(step);
            if (command?.Kind == CommandKind.EditOrder) { inEdit = true; risky.Add(step); continue; }
            if (inEdit && CommandParser.TryParseOrderEdit(step, out var change))
            {
                risky.Add(step);
                inEdit = change.Kind != "done";
                continue;
            }
            // Leaving edit mode: a later risky command is judged on its own, not hidden inside the edit chain.
            inEdit = false;
            (command is not null && ActionRisk.RiskyCommands.Contains(command.Kind) ? risky : safe).Add(step);
        }
        return (safe, risky);
    }

    /// <summary>Parks steps until the seller answers YES or NO; the echo of what was heard is shown with the question.</summary>
    private async Task ParkVoiceStepsAsync(string fromPhoneNumber, Seller seller, IReadOnlyList<string> steps, string heard, CancellationToken ct)
    {
        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);
        ctx.PendingVoiceSteps = steps.ToList();
        ctx.PendingVoiceParkedAt = DateTime.UtcNow;
        ctx.PendingVoiceLogId = await NewestActionLogIdAsync(seller.Id, ct);
        SetState(session, ConversationState.AwaitingVoiceConfirmation);
        await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get("media.002", heard), ct);
        await PersistAsync(session, ctx, ct);
    }

    private static bool IsOrderDraftState(ConversationState state) =>
        state is ConversationState.AwaitingOrderConfirmation or ConversationState.AwaitingOrderMissingFields;

    /// <summary>
    /// Runs the voice steps that waited for an order draft, once the seller's answer to that draft has returned the session to Idle.
    /// Only when the message just handled was answering a draft, so an unrelated later message never replays them.
    /// </summary>
    /// <summary>
    /// The open order draft was put on hold by an unrelated message. The voice steps waiting for it stay parked: they run when the draft is
    /// answered (or expire), and the seller is shown what is waiting.
    /// </summary>
    private async Task HoldParkedVoiceStepsAsync(string fromPhoneNumber, CancellationToken ct)
    {
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);
        if (ctx.PendingVoiceContinue is not { Count: > 0 } waiting) return;

        await _sender.SendTextMessageAsync(fromPhoneNumber,
            MessageTemplates.Get("voice-confirm.004", string.Join("  →  ", waiting.Select(s => $"\"{s}\""))), ct);
    }

    private async Task ResumeVoiceContinueAsync(string fromPhoneNumber, bool answeredDraft, CancellationToken ct)
    {
        if (!answeredDraft) return;
        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        var session = seller.Session!;
        var ctx = SessionContextData.FromJson(session.ContextJson);
        if (ctx.PendingVoiceContinue is not { Count: > 0 } rest || session.State != ConversationState.Idle) return;

        // Steps parked long ago belong to an old note: a draft answered much later must not replay them.
        var stale = ctx.PendingVoiceContinueAt is null || DateTime.UtcNow - ctx.PendingVoiceContinueAt.Value > VoiceConfirmWindow;
        ctx.PendingVoiceContinue = null;
        ctx.PendingVoiceContinueAt = null;
        await PersistAsync(session, ctx, ct);
        if (stale)
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber,
                MessageTemplates.Get("voice-confirm.010", string.Join("  →  ", rest.Select(s => $"\"{s}\""))), ct);
            return;
        }
        await RunVoiceStepsAsync(fromPhoneNumber, rest, ct);
    }

    /// <summary>A parked YES is valid for this long; after that the seller must say the instruction again.</summary>
    private static readonly TimeSpan VoiceConfirmWindow = TimeSpan.FromMinutes(10);

    private async Task<int> NewestActionLogIdAsync(int sellerId, CancellationToken ct) =>
        await _db.ActionLogs.Where(l => l.SellerId == sellerId).MaxAsync(l => (int?)l.Id, ct) ?? 0;

    /// <summary>Runs the steps one after another as separate typed messages; stops (and says so) if "edit order" did not open edit mode.</summary>
    private async Task RunVoiceStepsAsync(string fromPhoneNumber, IReadOnlyList<string> steps, CancellationToken ct)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            await HandleIncomingMessageAsync(fromPhoneNumber, steps[i], ct);
            if (i == steps.Count - 1) return;

            // An order draft is answered by the seller (Save / Edit / Cancel) before the next order is started: the rest waits here.
            var session = (await LoadOrCreateSellerAsync(fromPhoneNumber, ct)).Session!;
            if (IsOrderDraftState(session.State))
            {
                var ctx = SessionContextData.FromJson(session.ContextJson);
                ctx.PendingVoiceContinue = steps.Skip(i + 1).ToList();
                ctx.PendingVoiceContinueAt = DateTime.UtcNow;
                await PersistAsync(session, ctx, ct);
                return;
            }

            if (CommandParser.TryParse(steps[i])?.Kind != CommandKind.EditOrder) continue;

            var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
            if (seller.Session!.State == ConversationState.AwaitingOrderEdit) continue;

            await _sender.SendTextMessageAsync(fromPhoneNumber,
                MessageTemplates.Get("voice-confirm.001"), ct);
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
        var parkedAt = ctx.PendingVoiceParkedAt;
        var parkedLogId = ctx.PendingVoiceLogId;
        ctx.PendingVoiceSteps = null;
        ctx.PendingVoiceParkedAt = null;
        SetState(session, ConversationState.Idle);
        await PersistAsync(session, ctx, ct);

        var message = (rawMessage ?? "").Trim();
        if (steps is { Count: > 0 } && CommandParser.IsAffirmative(message))
        {
            // The YES approves what was read out, against the orders as they were then. If time passed or anything changed meanwhile, drop it.
            var stale = parkedAt is null || DateTime.UtcNow - parkedAt.Value > VoiceConfirmWindow
                        || parkedLogId != await NewestActionLogIdAsync(seller.Id, ct);
            if (stale)
            {
                await _sender.SendTextMessageAsync(fromPhoneNumber,
                    MessageTemplates.Get("voice-confirm.002"), ct);
                await _db.SaveChangesAsync(ct);
                return true;
            }

            await RunVoiceStepsAsync(fromPhoneNumber, steps, ct);
            return true;
        }

        if (CommandParser.IsNegative(message) || message.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get("voice-confirm.003"), ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        return false;
    }
}
