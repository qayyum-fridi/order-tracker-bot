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
