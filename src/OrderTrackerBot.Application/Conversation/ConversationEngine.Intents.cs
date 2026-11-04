using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
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

    private async Task<RoutedIntent?> RouteIntentAsync(string message, CancellationToken ct, Seller? seller = null)
    {
        // With the flag on and a seller known, the router also gets the seller's context (what kind of shop, its products, what the bot was doing, the last messages).
        var route = seller is not null && _features.RouterContext
            ? await _ai.RouteIntentWithContextAsync(message, IntentCatalog.All, await BuildRouteContextAsync(seller, message, ct), ct)
            : await _ai.RouteIntentAsync(message, IntentCatalog.All, ct);
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

    /// <summary>
    /// What the router is told about this seller: the kind of shop, up to 15 of the products (the ones sharing a word with the message first, then the newest), what the bot was
    /// doing and the last three messages. Names and text only (no prices, no phone numbers beyond what the seller wrote); nothing here ever becomes a slot value.
    /// </summary>
    private async Task<AiRouteContext> BuildRouteContextAsync(Seller seller, string message, CancellationToken ct)
    {
        var words = message.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToHashSet();
        var products = (await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).OrderByDescending(p => p.Id).Take(80).Select(p => p.Name).ToListAsync(ct))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var related = products.Where(n => n.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => words.Contains(w))).ToList();
        var names = related.Concat(products.Except(related, StringComparer.OrdinalIgnoreCase)).Take(15).ToList();
        var recent = (await _db.MessageLogs.Where(m => m.Phone == seller.WhatsAppPhoneNumber && m.Direction == "inbound").OrderByDescending(m => m.Id).Take(6).Select(m => m.RawText).ToListAsync(ct))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !t.StartsWith('[') && !string.Equals(t.Trim(), message.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(3).Reverse().Select(t => t.Length > 160 ? t[..160] : t).ToList();
        return new AiRouteContext
        {
            ShopType = seller.BusinessType,
            ProductNames = names,
            Situation = seller.Session is null ? null : DescribeVoiceSituation(seller, SessionContextData.FromJson(seller.Session.ContextJson)),
            RecentMessages = recent,
        };
    }

    /// <summary>Text path: runs the routed command, or sends its how-to. False when the catalog did not place the message.</summary>
    private async Task<bool> TryRunRoutedIntentAsync(Seller seller, ConversationSession session, SessionContextData ctx, string message, CancellationToken ct)
    {
        if (await RouteIntentAsync(message, ct, seller) is not { } routed) return false;
        if (routed.Command is not null && CommandParser.TryParse(routed.Command) is { } command)
            await ExecuteCommandAsync(seller, session, ctx, command, ct);
        else
            await ReplyAsync(seller, MessageTemplates.Get("intent.howto", routed.Help!), ct);
        return true;
    }
}
