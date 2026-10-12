namespace OrderTrackerBot.Application.Conversation;

/// <summary>What the engine does with one seller turn: run it, ask for a missing or unclear detail, or refuse without changing anything.</summary>
public enum DecisionKind { Execute, Clarify, Reject }

/// <summary>
/// The outcome of the safety gate. Steps are only set for <see cref="DecisionKind.Execute"/>; Message is the question or reply for the other two.
/// </summary>
public sealed record TurnDecision(DecisionKind Kind, IReadOnlyList<string> Steps, string? Message = null, IReadOnlyList<string>? Options = null)
{
    public static TurnDecision Execute(IReadOnlyList<string> steps) => new(DecisionKind.Execute, steps);

    public static TurnDecision Clarify(string question, IReadOnlyList<string>? options = null) =>
        new(DecisionKind.Clarify, Array.Empty<string>(), question, options ?? Array.Empty<string>());

    public static TurnDecision Reject(string reply) => new(DecisionKind.Reject, Array.Empty<string>(), reply);
}

/// <summary>
/// The safety gate every idle turn passes, typed or spoken (voice steps are replayed as typed messages, so they reach it too).
/// A typed message is the seller's own words, so it is not checked for faithfulness; it is checked for being taken back.
/// </summary>
public static class TurnGate
{
    public const string RetractedReply =
        "Yeh instruction wapas lete hue lag raha hai (nahi/mat/galat), is liye kuch nahi badla. Chalana ho to saaf alfaz mein dobara likhein.";

    public static TurnDecision DecideTyped(string message) =>
        ActionRisk.IsRetractedCommand(message) ? TurnDecision.Reject(RetractedReply) : TurnDecision.Execute(new[] { message });
}
