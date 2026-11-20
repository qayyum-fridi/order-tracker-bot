using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// A product screenshot names its product the website's way ("3 PIECE - EMBROIDERED PREMIUM KHADDAR SUIT") while the shop already sells it as
// "Khaddar Suit". When the screenshot shows a colour or size and a catalog name's words are all in its name, the bot asks whether it is a variant
// of that product (taps: the catalog product, or "Naya product"). Picking the product saves the screenshot under that name, so it becomes one more
// colour / size of it; either way the usual YES confirmation follows and nothing is saved before it.
public partial class ConversationEngine
{
    private static readonly HashSet<string> VariantNameNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "pc", "pcs", "piece", "pieces", "set", "the", "and", "with", "new", "of", "a",
    };

    private static readonly Regex VariantNameWord = new(@"\p{L}{2,}", RegexOptions.Compiled);

    /// <summary>The words of a product name that say what it is: letters only, lower case, without counts and filler ("3 piece", "set").</summary>
    internal static HashSet<string> VariantNameWords(string name) =>
        VariantNameWord.Matches(name).Select(m => m.Value.ToLowerInvariant()).Where(w => !VariantNameNoise.Contains(w)).ToHashSet();

    /// <summary>
    /// Catalog names that the screenshot's name is a longer form of: every word of the catalog name is in it. A one-word catalog name counts only
    /// when the screenshot's name is short too (otherwise "Suit" would match every suit). The same name is not a match. Longest first, at most two.
    /// </summary>
    internal static List<string> VariantNameMatches(string screenshotName, IEnumerable<string> catalogNames)
    {
        var shot = VariantNameWords(screenshotName);
        return catalogNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !string.Equals(n.Trim(), screenshotName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(n => (Name: n, Words: VariantNameWords(n)))
            .Where(c => c.Words.Count >= 2 || (c.Words.Count == 1 && shot.Count <= 2))
            .Where(c => c.Words.All(shot.Contains))
            .OrderByDescending(c => c.Words.Count).ThenBy(c => c.Name)
            .Select(c => c.Name).Take(2).ToList();
    }

    /// <summary>What makes the screenshot a variant: its colour(s) and size(s) as the seller will read them ("Green", "size M, L"); null when none.</summary>
    internal static string? VariantLabel(AiNewProduct product)
    {
        var parts = new List<string>();
        var colour = product.Colors.Count > 0 ? string.Join(" / ", product.Colors)
            : product.Attributes.FirstOrDefault(a => a.Key.Trim().ToLowerInvariant() is "color" or "colour").Value;
        if (!string.IsNullOrWhiteSpace(colour)) parts.Add(colour.Trim());
        if (product.Sizes.Count > 0) parts.Add("size " + string.Join(", ", product.Sizes));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>True when the variant question was sent.</summary>
    private async Task<bool> TryAskVariantMatchAsync(Seller seller, ConversationSession session, SessionContextData ctx, TurnAction action, CancellationToken ct)
    {
        var product = action.Products[0];
        if (VariantLabel(product) is not { } label) return false;
        var catalog = await _db.Products.Where(p => p.SellerId == seller.Id && p.IsActive).Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var matches = VariantNameMatches(product.Name, catalog.Select(p => p.Name));
        if (matches.Count == 0) return false;

        var options = matches
            .Select(name => new ChoiceOption(name, "~" + catalog.First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Id))
            .Append(new ChoiceOption(MessageTemplates.Get("variant.new"), "~new"))
            .ToList();
        AwaitGuidedChoice(session, ctx, action, product.Name, options);
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, matches.Count == 1
            ? MessageTemplates.Get("variant.match.one", matches[0], label)
            : MessageTemplates.Get("variant.match.many", label), options, ct);
        return true;
    }

    /// <summary>The answer to the variant question: the picked catalog product's name replaces the screenshot's, or "~new" keeps it. Then the usual YES.</summary>
    private async Task RunVariantPickAsync(Seller seller, ConversationSession session, SessionContextData ctx, QueuedModelTurn turn, string pick, CancellationToken ct)
    {
        var action = turn.Action;
        if (pick != "~new" && int.TryParse(pick[1..], out var id)
            && await _db.Products.FirstOrDefaultAsync(p => p.Id == id && p.SellerId == seller.Id && p.IsActive, ct) is { } existing)
            action = WithProductName(action, existing.Name);
        await ProposeScreenshotProductsAsync(seller, session, ctx, action, ct, askVariant: false);
    }

    /// <summary>The same screenshot product under another name; every other detail (price, colour, sizes, fabric ...) is kept as read.</summary>
    private static TurnAction WithProductName(TurnAction action, string name)
    {
        var node = JsonSerializer.SerializeToNode(action.Products[0])!.AsObject();
        var key = node.Select(kv => kv.Key).First(k => k.Equals(nameof(AiNewProduct.Name), StringComparison.OrdinalIgnoreCase));
        node[key] = name;
        return new TurnAction { Action = action.Action, Products = new List<AiNewProduct> { node.Deserialize<AiNewProduct>()! } };
    }
}
