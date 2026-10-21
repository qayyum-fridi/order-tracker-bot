using OrderTrackerBot.Application.Templates;
namespace OrderTrackerBot.Application.Conversation;

/// <summary>What the engine does with one seller turn: run it, ask for a missing or unclear detail, or refuse without changing anything.</summary>
public enum DecisionKind { Execute, Clarify, Reject }

/// <summary>
/// The outcome of the safety gate. Steps are run only for <see cref="DecisionKind.Execute"/> (the other two may carry them as a suggestion for a YES); Message is the question or reply for the other two.
/// </summary>
public sealed record TurnDecision(DecisionKind Kind, IReadOnlyList<string> Steps, string? Message = null, IReadOnlyList<string>? Options = null)
{
    public static TurnDecision Execute(IReadOnlyList<string> steps) => new(DecisionKind.Execute, steps);

    public static TurnDecision Clarify(string question, IReadOnlyList<string>? options = null) =>
        new(DecisionKind.Clarify, Array.Empty<string>(), question, options ?? Array.Empty<string>());

    public static TurnDecision Reject(string reply) => new(DecisionKind.Reject, Array.Empty<string>(), reply);

    /// <summary>The same two refusals, but carrying what the voice rewrite WOULD have run, so the seller can be shown it and say YES instead of hitting a dead end.</summary>
    public static TurnDecision ClarifyWithSteps(string question, IReadOnlyList<string> steps) =>
        new(DecisionKind.Clarify, steps, question, Array.Empty<string>());

    public static TurnDecision RejectWithSteps(string reply, IReadOnlyList<string> steps) => new(DecisionKind.Reject, steps, reply);
}

/// <summary>
/// The safety gate every idle turn passes, typed or spoken (voice steps are replayed as typed messages, so they reach it too).
/// A typed message is the seller's own words, so it is not checked for faithfulness; it is checked for being taken back.
/// </summary>
public static class TurnGate
{
    public static readonly string RetractedReply =
        MessageTemplates.Get("turn-decision.001");

    public static TurnDecision DecideTyped(string message) =>
        ActionRisk.IsRetractedCommand(message) ? TurnDecision.Reject(RetractedReply) : TurnDecision.Execute(new[] { message });
}
