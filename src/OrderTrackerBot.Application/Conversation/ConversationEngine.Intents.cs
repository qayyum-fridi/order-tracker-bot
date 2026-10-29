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

    /// <summary>What the router decided for one message: a typed command to run, or the how-to reply for a missing detail.</summary>
    private sealed record RoutedIntent(string? Command, string? Help);

    private async Task<RoutedIntent?> RouteIntentAsync(string message, CancellationToken ct)
    {
        var route = await _ai.RouteIntentAsync(message, IntentCatalog.All, ct);
        if (route is null || route.Confidence < MinRouteConfidence || IntentCatalog.Find(route.Intent) is not { } intent) return null;

        var built = IntentCatalog.Build(intent, route.Slots);
        if (built.Command is null)
        {
            _logger?.LogInformation("Intent {Intent} routed; missing slots: {Missing}", intent.Id, string.Join(",", built.Missing));
            return intent.Help is null ? null : new RoutedIntent(null, intent.Help);
        }

        // The built command must be one the parser reads as a command, and never a risky one: those need the seller's own typed words.
        if (CommandParser.TryParse(built.Command) is not { } parsed || ActionRisk.RiskyCommands.Contains(parsed.Kind)) return null;
        _logger?.LogInformation("Intent {Intent} routed to {Kind}", intent.Id, parsed.Kind);
        return new RoutedIntent(built.Command, null);
    }

    /// <summary>Text path: runs the routed command, or sends its how-to. False when the catalog did not place the message.</summary>
    private async Task<bool> TryRunRoutedIntentAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (await RouteIntentAsync(message, ct) is not { } routed) return false;
        if (routed.Command is not null && CommandParser.TryParse(routed.Command) is { } command)
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
        else
            await ReplyAsync(seller, MessageTemplates.Get(routed.Help!), ct);
        return true;
    }
}
