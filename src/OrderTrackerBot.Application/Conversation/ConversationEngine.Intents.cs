using Microsoft.Extensions.Logging;
using OrderTrackerBot.Application.Intents;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// A message the order reader could not place is offered to the intent catalog: the AI picks one action and its slots, the engine builds
// the typed command and runs it through the same parser as typed text. A typed command never comes from the model.
public partial class ConversationEngine
{
    /// <summary>Below this the router's guess is not trusted: the seller gets the usual "not understood" reply.</summary>
    private const double MinRouteConfidence = 0.7;

    /// <summary>What the router decided for one message: a typed command to run, or the line the seller should type (<c>Help</c>).</summary>
    private sealed record RoutedIntent(string? Command, string? Help);

    private async Task<RoutedIntent?> RouteIntentAsync(string message, CancellationToken ct)
    {
        var route = await _ai.RouteIntentAsync(message, IntentCatalog.All, ct);
        if (route is null || route.Confidence < MinRouteConfidence || IntentCatalog.Find(route.Intent) is not { } intent) return null;

        var built = IntentCatalog.Build(intent, route.Slots);
        // A missing detail, or a command that would change money or delete data, is never run from a guess: the seller gets the exact line to type.
        if (built.Command is null) return new RoutedIntent(null, IntentCatalog.Example(intent));

        // The built command must parse to the kind this entry declares, or it is not the action the seller was offered.
        if (CommandParser.TryParse(built.Command) is not { } parsed || parsed.Kind.ToString() != intent.Kind) return null;
        _logger?.LogInformation("Intent {Intent} routed to {Kind}", intent.Id, parsed.Kind);
        if (ActionRisk.RiskyCommands.Contains(parsed.Kind)) return new RoutedIntent(null, built.Command);
        return new RoutedIntent(built.Command, null);
    }

    /// <summary>Text path: runs the routed command, or sends its how-to. False when the catalog did not place the message.</summary>
    private async Task<bool> TryRunRoutedIntentAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (await RouteIntentAsync(message, ct) is not { } routed) return false;
        if (routed.Command is not null && CommandParser.TryParse(routed.Command) is { } command)
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
        else
            await ReplyAsync(seller, MessageTemplates.Get("intent.howto", routed.Help!), ct);
        return true;
    }
}
