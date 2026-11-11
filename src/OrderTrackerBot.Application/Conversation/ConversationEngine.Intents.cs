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

    /// <summary>Below the route bar but at least this sure, the router's guess is offered as a Yes / No question (ConversationEngine.WordFix.cs).</summary>
    private const double MinGuessConfidence = 0.4;

    /// <summary>The command the router guessed (no slots to fill) and its intent, for this turn only.</summary>
    private (string Command, string IntentId, double Confidence)? _turnGuess;

    /// <summary>What the router decided for one message: a typed command to run, or the line the seller should type (<c>Help</c>).</summary>
    /// <summary><see cref="Reply"/> is a finished seller message (a how-to answer); otherwise <see cref="Help"/> is the line to type, shown with <c>intent.howto</c>.</summary>
    private sealed record RoutedIntent(string? Command, string? Help, string? Reply = null);

    /// <summary>The message the seller gets for a routed intent that runs no command.</summary>
    private static string RoutedReply(RoutedIntent routed) => routed.Reply ?? MessageTemplates.Get("intent.howto", routed.Help!);

    /// <summary>A how-to word has to be this well matched (see <see cref="IntentCatalog.Closest"/>) before the closest command is named.</summary>
    private const double MinHowToScore = 0.5;

    private async Task<RoutedIntent?> RouteIntentAsync(string message, CancellationToken ct, Seller? seller = null)
    {
        // "How do I ...?" is answered with the line to type, never run: a question must not change data. The how-to words are the keywords of the catalog's guide.howto entry.
        var isHowTo = IsHowToQuestion(message);
        if (isHowTo)
        {
            if (HowToRoute(message) is { } howTo) return howTo;
        }
        else
        {
            // A message that only one intent's catalog keywords claim, and that needs no slot, runs without a model call.
            if (DirectKeywordRoute(message) is { } direct) return direct;
            // Words this seller taught (a tap or a confirmed guess) route their own next message, without waiting for other sellers.
            if (seller is not null && await SellerTaughtRouteAsync(seller, message, ct) is { } taught) return taught;
            // Words that three or more sellers agreed on route the same way (only when they all point to one intent).
            if (LearnedKeywordLog.HintsFor(IntentKeywords.Tokens(message)).Distinct(StringComparer.Ordinal).ToList() is { Count: 1 } shared
                && TaughtIntentRoute(shared[0]) is { } shareRoute) return shareRoute;
        }

        // With the flag on and a seller known, the router also gets the seller's context (what kind of shop, its products, what the bot was doing, the last messages).
        var route = seller is not null && _features.RouterContext
            ? await _ai.RouteIntentWithContextAsync(message, IntentCatalog.All, await BuildRouteContextAsync(seller, message, ct), ct)
            : await _ai.RouteIntentAsync(message, IntentCatalog.All, ct);
        // A guess the router is not sure of (between the two bars) is offered to the seller as a Yes / No tap instead of run.
        if (!isHowTo && route is not null && route.Confidence >= MinGuessConfidence && route.Confidence < MinRouteConfidence
            && IntentCatalog.Find(route.Intent) is { } guessed && guessed.Id != IntentCatalog.HowToId
            && IntentCatalog.Build(guessed, new Dictionary<string, string>()).Command is { } guessCommand)
            _turnGuess = (guessCommand, guessed.Id, route.Confidence);
        // Every router answer is kept (intent, confidence, what the bot did with it) so the two bars can be tuned from real use.
        if (route is not null)
            _db.RouterDecisions.Add(new RouterDecision
            {
                IntentId = route.Intent.Length > 80 ? route.Intent[..80] : route.Intent,
                Confidence = route.Confidence,
                Outcome = route.Confidence >= MinRouteConfidence ? "run" : route.Confidence >= MinGuessConfidence ? "guess" : "ignored",
            });
        // A how-to question is never left with "not understood": the closest command was tried above, so say where to look.
        if (route is null || route.Confidence < MinRouteConfidence || IntentCatalog.Find(route.Intent) is not { } intent)
            return isHowTo ? HowToFallback(message) : null;
        // The model placed the question on the catalog's own how-to entry: name the closest command, or say where to look.
        if (intent.Id == IntentCatalog.HowToId)
            return HowToRoute(message) ?? HowToFallback(message);

        var built = IntentCatalog.Build(intent, route.Slots);
        // A question is answered with the line to type, not by doing it (except an entry that is only help or contact, which is what was asked for).
        if (isHowTo && !intent.RunOnHowTo) return new RoutedIntent(null, null, HowToReply(new[] { intent }));
        // A help or contact entry may run on a how-to question only when its own words are in the message. The how-to word alone ("کیسے") is not a request to contact support.
        if (isHowTo && intent.RunOnHowTo && IntentKeywords.Match(message, new[] { intent }).Count == 0) return HowToFallback(message);
        // A missing detail, or a command that would change money or delete data, is never run from a guess: the seller gets the exact line to type.
        if (built.Command is null) return new RoutedIntent(null, IntentCatalog.Example(intent));

        // The built command must parse to the kind this entry declares, or it is not the action the seller was offered.
        if (CommandParser.TryParse(built.Command) is not { } parsed || parsed.Kind.ToString() != intent.Kind) return null;
        _logger?.LogInformation("Intent {Intent} routed to {Kind}", intent.Id, parsed.Kind);
        await LearnRoutedWordsAsync(seller, message, intent.Id, ct);
        if (ActionRisk.RiskyCommands.Contains(parsed.Kind)) return new RoutedIntent(null, built.Command);
        return new RoutedIntent(built.Command, null);
    }

    /// <summary>
    /// A seller's own taught words: when every word of the message that this seller voted for points at one intent, and that intent needs no
    /// slot, the message runs that intent. Only this seller's votes are used.
    /// </summary>
    private async Task<RoutedIntent?> SellerTaughtRouteAsync(Seller seller, string message, CancellationToken ct)
    {
        var words = IntentKeywords.Tokens(message).ToList();
        if (words.Count == 0) return null;
        var taught = (await _db.LearnedKeywordVotes.AsNoTracking()
                .Where(v => v.SellerId == seller.Id && words.Contains(v.Word))
                .Select(v => v.IntentId).ToListAsync(ct))
            .Distinct().ToList();
        return taught.Count == 1 ? TaughtIntentRoute(taught[0]) : null;
    }

    /// <summary>
    /// The route for an intent that learned words point to: only when it needs no slot, its command parses to its kind, and it is not risky.
    /// Risky intents are returned as the line to type, never run.
    /// </summary>
    private RoutedIntent? TaughtIntentRoute(string intentId)
    {
        if (IntentCatalog.Find(intentId) is not { } intent || intent.Id == IntentCatalog.HowToId || RunnableRoute(intent) is not { } route) return null;

        _turnRouteIntent = intent.Id;
        return route;
    }

    /// <summary>The route for a message that exactly one intent's catalog keywords claim, when that intent needs no slot; otherwise null (the AI decides).</summary>
    private RoutedIntent? DirectKeywordRoute(string message)
    {
        var hits = IntentKeywords.Match(message, IntentCatalog.All);
        // "Customers support ka document do": a single word ("customers") and a phrase ("customers support") both match; the phrase is what was meant.
        if (hits.Count > 1 && IntentKeywords.MatchPhrase(message, hits.Select(IntentCatalog.Find).OfType<IntentDefinition>()) is { Count: 1 } phrase)
            hits = phrase;
        return hits.Count == 1 && IntentCatalog.Find(hits[0]) is { } intent && intent.Id != IntentCatalog.HowToId ? RunnableRoute(intent) : null;
    }

    /// <summary>The route for an intent that needs no slot: its command when it parses to the entry's kind (the line to type when risky); otherwise null.</summary>
    private static RoutedIntent? RunnableRoute(IntentDefinition intent)
    {
        if (intent.Slots.Any(s => s.Required)) return null;
        var built = IntentCatalog.Build(intent, new Dictionary<string, string>());
        if (built.Command is null || CommandParser.TryParse(built.Command) is not { } parsed || parsed.Kind.ToString() != intent.Kind) return null;
        if (ActionRisk.RiskyCommands.Contains(parsed.Kind)) return new RoutedIntent(null, built.Command);
        return new RoutedIntent(built.Command, null);
    }

    /// <summary>True when the message carries one of the how-to words (the keywords of the guide.howto entry): "kaise", "how do I", "طریقہ"...</summary>
    private static bool IsHowToQuestion(string message) =>
        IntentCatalog.Find(IntentCatalog.HowToId) is { } howTo && IntentKeywords.MatchTolerant(message, new[] { howTo }).Count > 0;

    /// <summary>
    /// The answer to a how-to question: the closest command (a second one when it is nearly as close) with the line to type and a worked example.
    /// A help or contact entry is run instead, since that is what the seller asked for. Null when nothing in the catalog is close enough.
    /// </summary>
    private RoutedIntent? HowToRoute(string message)
    {
        var closest = IntentCatalog.Closest(message);
        if (closest.Count == 0 || closest[0].Score < MinHowToScore) return null;

        var best = closest[0];
        if (best.Intent.RunOnHowTo && RunnableRoute(best.Intent) is { } run) return run;
        var shown = closest.Where(c => c.Score >= MinHowToScore && c.Score >= best.Score * 0.7 && !c.Intent.RunOnHowTo).Take(2).Select(c => c.Intent).ToList();
        _logger?.LogInformation("How-to question placed on {Intent}", best.Intent.Id);
        return new RoutedIntent(null, null, HowToReply(shown));
    }

    /// <summary>A how-to question nothing matched well: the weakly closest command is offered as a guess; with nothing close, the places to look.</summary>
    private const double MinHowToGuessScore = 0.1;

    private static RoutedIntent HowToFallback(string message)
    {
        var closest = IntentCatalog.Closest(message, 1);
        return closest.Count > 0 && closest[0].Score >= MinHowToGuessScore && !closest[0].Intent.RunOnHowTo
            ? new RoutedIntent(null, null, MessageTemplates.Get("guide.howto.guess", HowToLines(new[] { closest[0].Intent })))
            : new RoutedIntent(null, null, MessageTemplates.Get("guide.howto.none"));
    }

    private static string HowToLines(IReadOnlyList<IntentDefinition> intents) =>
        string.Join("\n\n", intents.Select(i =>
        {
            var line = IntentCatalog.Example(i);
            var sample = IntentCatalog.SampleCommand(i);
            return sample is null || sample == line ? line : MessageTemplates.Get("guide.howto.example", line, sample);
        }));

    /// <summary>The how-to message for one or two intents: the line to type, with a worked example when the entry has sample values.</summary>
    private static string HowToReply(IReadOnlyList<IntentDefinition> intents) =>
        MessageTemplates.Get("guide.howto.line", HowToLines(intents));

    /// <summary>
    /// Learns the words of a routed message for the intent the router chose. The seller's own product and customer names are left out,
    /// so no name reaches the list shared by all sellers.
    /// </summary>
    private async Task LearnRoutedWordsAsync(Seller? seller, string message, string intentId, CancellationToken ct)
    {
        _turnRouteIntent = intentId;
        // A vote needs a seller: each seller counts once per word, so an unknown sender cannot vote.
        if (seller is null) return;
        var words = await OwnFilteredWordsAsync(seller, IntentKeywords.Tokens(message), ct);
        _turnVotes.AddRange(await LearnedKeywordLog.AddVotesAsync(_db, seller.Id, words, intentId, ct));
    }

    /// <summary>
    /// An order cancelled or edited within an hour of being saved was routed wrongly (a proxy): the order is marked, and the votes it produced
    /// are removed (see <see cref="LearnedKeywordLog.RetractAsync"/>).
    /// </summary>
    private async Task RecordCorrectionAsync(Seller seller, Order order, CancellationToken ct)
    {
        if (DateTime.UtcNow - order.CreatedAt > TimeSpan.FromHours(1)) return;
        order.CorrectedWithinHour = true;
        await LearnedKeywordLog.RetractAsync(_db, seller.Id, order.Id, ct);
    }

    /// <summary>The words without the seller's own product and customer names, so no name reaches the shared votes.</summary>
    private async Task<HashSet<string>> OwnFilteredWordsAsync(Seller seller, HashSet<string> words, CancellationToken ct)
    {
        if (words.Count == 0) return words;
        var products = await _db.Products.Where(p => p.SellerId == seller.Id).OrderByDescending(p => p.Id).Select(p => p.Name).Take(500).ToListAsync(ct);
        var customers = await _db.Customers.Where(c => c.SellerId == seller.Id).OrderByDescending(c => c.Id).Select(c => c.Name).Take(500).ToListAsync(ct);
        words.ExceptWith(products.Concat(customers).SelectMany(IntentKeywords.Tokens));
        return words;
    }

    /// <summary>The votes this turn added (linked to the order the turn saves) and the intent the router or a tap chose.</summary>
    private List<LearnedKeywordVote> _turnVotes = new();
    private string? _turnRouteIntent;

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
        var situation = seller.Session is null ? null : DescribeVoiceSituation(seller, SessionContextData.FromJson(seller.Session.ContextJson));
        // Words this message shares with the shared list tell the router what they usually mean (only words with enough votes).
        var learned = LearnedKeywordLog.HintsFor(IntentKeywords.Tokens(message));
        if (learned.Count > 0) situation = $"{situation} The words in this message usually mean: {string.Join(", ", learned)}.".Trim();
        return new AiRouteContext
        {
            ShopType = seller.BusinessType,
            ProductNames = names,
            Situation = situation,
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
            await ReplyAsync(seller, RoutedReply(routed), ct);
        return true;
    }
}
