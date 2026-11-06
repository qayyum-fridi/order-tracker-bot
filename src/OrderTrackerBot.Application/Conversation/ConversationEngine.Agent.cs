using System.Text.RegularExpressions;
using OrderTrackerBot.Application.Agent;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// The planner path (Features:Agent). A message the order reader and the intent catalog could not place goes to ONE planning call that sees the seller's own
// data (SellerContext). The plan is checked by PlanVerifier (ids, amounts, ambiguity), compiled into typed messages, and those run through the same
// pipeline as typed text, so every handler's rules (undo, stock, price guard, confirmation) still apply. Steps that change existing data wait for YES.
public partial class ConversationEngine
{
    /// <summary>Below this the plan is not trusted and the seller gets the usual "not understood" path.</summary>
    private const double MinPlanConfidence = 0.7;

    /// <summary>How long "I will send them one by one" stays open without another matching message.</summary>
    private static readonly TimeSpan AgentAwaitingWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The seller answers the planner's question ("3000" after "Shawl add karo"): the answer is read together with the request it completes. Only inside the
    /// same 10-minute window; a real command in the answer drops the request and runs as usual.
    /// </summary>
    private async Task<bool> TryResumeAgentPendingAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.AgentPending is not { } pending) return false;
        ctx.AgentPending = null;
        await PersistAsync(session, ctx, ct);
        if (!_features.Agent || DateTime.UtcNow - pending.At > AgentAwaitingWindow || CommandParser.TryParse(message) is not null) return false;
        return await TryPlanAgentAsync(seller, session, ctx, $"{pending.Message}. {message}", ct);
    }

    /// <summary>A plan ready to run after the current turn: steps to run now, steps that wait for YES, and the step that could not be decided.</summary>
    private sealed record AgentRun(IReadOnlyList<string> Now, IReadOnlyList<string> Later, VerifiedStep? Stop, string Heard, string Message);

    private AgentRun? _agentRun;

    private static readonly Regex AgentDoneWords = new(@"^\s*(?:done|bas|bas\s+ho\s+gaya|khatam|ho\s+gaya|finish(?:ed)?|that'?s\s+all|بس|ختم|ہو\s+گیا)\s*[.!]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private async Task<bool> TryPlanAgentAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (!_features.Agent || !seller.OnboardingComplete || session.State != ConversationState.Idle) return false;

        var context = await new SellerContextBuilder(_db).BuildAsync(seller, message, ct);
        var plan = await _ai.PlanAsync(message, context.Render(), AgentTools.All, ct);
        if (plan is null || plan.Confidence < MinPlanConfidence) return false;

        // "3 products add karne hain, ek ek karke bhejta hun": remember it, and ask for the first one. Only when nothing else is planned: a model that
        // plans a step AND an "awaiting" is repeating a missing-detail question (seen live), and would ask for a price the seller already gave.
        if (plan.Steps.Count == 0 && plan.Awaiting is { Tool: "add_product" } awaiting)
        {
            ctx.AgentAwaiting = new AgentAwaitingData { Tool = awaiting.Tool, Remaining = awaiting.Count };
            await PersistAsync(session, ctx, ct);
        }

        if (plan.Steps.Count == 0)
        {
            if (plan.Question is null && ctx.AgentAwaiting is null) return false;
            // A question about a missing detail keeps the request, so the seller's answer completes it (see TryResumeAgentPendingAsync).
            if (plan.Question is not null && plan.Awaiting is null)
            {
                ctx.AgentPending = new AgentPendingData { Message = message, Question = plan.Question, At = DateTime.UtcNow };
                await PersistAsync(session, ctx, ct);
            }
            await ReplyAsync(seller, plan.Question ?? MessageTemplates.Get("agent.008"), ct);
            return true;
        }

        var verified = await new PlanVerifier(_db).VerifyAsync(seller, plan, context, message, ct);
        var compiled = await new AgentStepCompiler(_db).CompileAsync(seller, verified, ct);
        var firstConfirm = compiled.Steps.ToList().FindIndex(s => s.Confirm);
        var now = (firstConfirm < 0 ? compiled.Steps : compiled.Steps.Take(firstConfirm)).Select(s => s.Text).ToList();
        var later = firstConfirm < 0 ? new List<string>() : compiled.Steps.Skip(firstConfirm).Select(s => s.Text).ToList();
        if (now.Count == 0 && later.Count == 0 && compiled.Stop is null) return false;

        var heard = MessageTemplates.Get("agent.007", string.Join("  →  ", now.Concat(later).Select(s => $"\"{s}\"")));
        _agentRun = new AgentRun(now, later, compiled.Stop, heard, message);
        return true;
    }

    /// <summary>Runs the plan after the turn that made it: safe steps now, the rest parked for YES (reusing the voice-note machinery), then the question for the step that could not be decided.</summary>
    private async Task RunAgentPlanAsync(string fromPhoneNumber, CancellationToken ct)
    {
        var run = _agentRun!;
        _agentRun = null;
        var sentAt = _sentAtUtc;

        var seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        if (run.Now.Count == 0 && run.Later.Count > 0)
        {
            await ParkVoiceStepsAsync(fromPhoneNumber, seller, run.Later, run.Heard, ct);
            return;
        }

        if (run.Now.Count > 0 || run.Stop is not null) await _sender.SendTextMessageAsync(fromPhoneNumber, run.Heard, ct);
        if (run.Now.Count > 0) await RunVoiceStepsAsync(fromPhoneNumber, run.Now, sentAt, ct);

        seller = await LoadOrCreateSellerAsync(fromPhoneNumber, ct);
        if (run.Later.Count > 0)
        {
            // An order draft left open by the safe steps keeps its state; the steps that need YES are not run behind it.
            if (seller.Session!.State == ConversationState.Idle) await ParkVoiceStepsAsync(fromPhoneNumber, seller, run.Later, run.Heard, ct);
            else await _sender.SendTextMessageAsync(fromPhoneNumber, MessageTemplates.Get("voice-confirm.005"), ct);
            return;
        }
        if (run.Stop is { } stop)
        {
            await ReplyAsync(seller, AgentStopText(stop), ct);
            // A question (not a refusal): keep the request, so the seller's answer completes it instead of starting over.
            if (stop.Verdict == StepVerdict.Ask)
            {
                var session = seller.Session!;
                var ctx = SessionContextData.FromJson(session.ContextJson);
                ctx.AgentPending = new AgentPendingData { Message = run.Message, Question = AgentStopText(stop), At = DateTime.UtcNow };
                await PersistAsync(session, ctx, ct);
            }
        }
    }

    private static string AgentStopText(VerifiedStep stop) => stop.Reason switch
    {
        "ambiguous_customer" or "ambiguous_product" => MessageTemplates.Get("agent.001", string.Join(", ", stop.Choices ?? Array.Empty<string>())),
        "price_missing" => MessageTemplates.Get("agent.003"),
        "customer_missing" => MessageTemplates.Get("agent.004"),
        "field_unclear" => MessageTemplates.Get("agent.009"),
        "relative_change" => MessageTemplates.Get("agent.010"),
        _ => MessageTemplates.Get("agent.002"),
    };

    /// <summary>
    /// While "I will send N products" is open: a product line is saved and counted, "done" closes it, any other real command leaves it and runs,
    /// and free text is left to the rest of the pipeline (the promise stays open until it expires).
    /// </summary>
    private async Task<bool> TryHandleAgentAwaitingAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (ctx.AgentAwaiting is not { } wait) return false;
        if (DateTime.UtcNow - wait.At > AgentAwaitingWindow)
        {
            ctx.AgentAwaiting = null;
            await PersistAsync(session, ctx, ct);
            return false;
        }

        if (AgentDoneWords.IsMatch(message))
        {
            ctx.AgentAwaiting = null;
            await PersistAsync(session, ctx, ct);
            await ReplyAsync(seller, MessageTemplates.Get("agent.006", wait.Done), ct);
            return true;
        }

        var command = CommandParser.TryParse(message);
        if (command is null) return false;
        if (command.Kind is not (CommandKind.AddProduct or CommandKind.AddProductsBulk))
        {
            ctx.AgentAwaiting = null;   // a different real command: the seller moved on
            await PersistAsync(session, ctx, ct);
            return false;
        }

        await ExecuteCommandAsync(seller, session, ctx, command, ct);
        wait.Done += command.Kind == CommandKind.AddProductsBulk
            ? Math.Max(1, (command.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length)
            : 1;
        wait.At = DateTime.UtcNow;
        var finished = wait.Remaining > 0 && wait.Done >= wait.Remaining;
        if (finished) ctx.AgentAwaiting = null;
        await PersistAsync(session, ctx, ct);
        if (finished) await ReplyAsync(seller, MessageTemplates.Get("agent.005", wait.Done), ct);
        return true;
    }
}
