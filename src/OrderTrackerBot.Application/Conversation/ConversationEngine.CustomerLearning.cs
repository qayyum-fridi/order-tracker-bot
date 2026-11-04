namespace OrderTrackerBot.Application.Conversation;

// Customer learning (MVP): when a name matches several customers the bot lists them. The seller then types the exact name; after
// the same pick twice for the same ambiguous name, that customer is used directly. Stored in the seller's session context.
public partial class ConversationEngine
{
    /// <summary>A pick is used from this many identical choices on.</summary>
    private const int CustomerPickVotes = 2;

    /// <summary>The customer id this seller has picked enough times for this ambiguous name, or null.</summary>
    private static int? LearnedCustomerFor(SessionContextData ctx, string key) =>
        ctx.LearnedCustomerIds is { } ids && ids.TryGetValue(key, out var id) && (ctx.LearnedCustomerVotes?.GetValueOrDefault(key) ?? 0) >= CustomerPickVotes
            ? id
            : null;

    /// <summary>Counts one pick. The same customer again adds a vote; a different one starts over (a wrong tap must not stick).</summary>
    private static void LearnCustomerPick(SessionContextData ctx, string key, int customerId)
    {
        var ids = ctx.LearnedCustomerIds ??= new Dictionary<string, int>();
        var votes = ctx.LearnedCustomerVotes ??= new Dictionary<string, int>();
        votes[key] = ids.TryGetValue(key, out var previous) && previous == customerId ? votes.GetValueOrDefault(key) + 1 : 1;
        ids[key] = customerId;
    }
}
