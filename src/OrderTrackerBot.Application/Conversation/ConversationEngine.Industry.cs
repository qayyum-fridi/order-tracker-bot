using System.Text.RegularExpressions;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

public sealed partial class ConversationEngine
{
    // "number 5 bola tha" (a voice note): the word "number" is required, so a product line such as "Shawl 3 piece" is never a list pick.
    private static readonly Regex SpokenIndustryNumber = new(@"(?:number|nambar|numbar|نمبر)\s*([1-7])(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The wording profile of this seller's kind of shop (their business type); the general one when not set.</summary>
    private static IndustryProfile Industry(Seller seller) => IndustryProfiles.Resolve(seller.BusinessType);

    /// <summary>
    /// "commands": the typed-command cheat sheet in four short messages. The wording is the Roman Urdu templates <c>cmdlist.001-004</c> (English / Urdu-script
    /// sellers get the hand-written translations through the usual sender); the examples come from the seller's kind of shop. Placeholders are numbered
    /// from zero without gaps per key (a test enforces it): see <see cref="CommandListArgs"/>. Read-only: nothing is saved.
    /// </summary>
    private async Task HandleCommandListAsync(Seller seller, CancellationToken ct)
    {
        var shop = Industry(seller);
        foreach (var key in new[] { "cmdlist.001", "cmdlist.002", "cmdlist.003", "cmdlist.004" })
            await ReplyAsync(seller, MessageTemplates.Get(key, CommandListArgs(key, shop)), ct);
    }

    /// <summary>Part 1: {0} product line, {1} full line, {2} name, {3} category. Part 2: {0} order example, {1} name. Parts 3 and 4 have no examples.</summary>
    internal static object[] CommandListArgs(string key, IndustryProfile shop) => key switch
    {
        "cmdlist.001" => new object[] { shop.MainLine, shop.FullProduct, shop.MainName, shop.Category },
        "cmdlist.002" => new object[] { shop.OrderExample, shop.MainName },
        _ => Array.Empty<object>()
    };

    /// <summary>The kind-of-shop question at setup: one tap list (row ids are the <c>industry &lt;key&gt;</c> commands). A typed answer that names no kind is asked again.</summary>
    private async Task AskIndustryAsync(Seller seller, string? notUnderstood, CancellationToken ct)
    {
        var intro = notUnderstood is null
            ? MessageTemplates.Get("industry.002")
            : MessageTemplates.Get("industry.003", notUnderstood);
        await SendChoicesAsync(seller.WhatsAppPhoneNumber, intro,
            IndustryProfiles.All.Append(IndustryProfiles.General).Select(p => new ChoiceOption(p.Title, $"industry {p.Key}")).ToList(), ct);
    }

    /// <summary>The profile for a tap (<c>industry grocery</c>), a button title or words like "kiryana"; null when nothing matches.</summary>
    private static IndustryProfile? PickIndustry(string message)
    {
        var text = message.Trim();
        if (CommandParser.TryParse(text) is { Kind: CommandKind.Industry, Text: { Length: > 0 } key })
            text = key;
        var all = IndustryProfiles.All.Append(IndustryProfiles.General).ToList();
        // The list is numbered 1-7 in this order: a typed number picks the same row.
        if (int.TryParse(text, out var number) && number >= 1 && number <= all.Count) return all[number - 1];
        if (SpokenIndustryNumber.Match(text) is { Success: true } spoken && int.Parse(spoken.Groups[1].Value) is var row && row <= all.Count)
            return all[row - 1];
        return all.FirstOrDefault(p => p.Key.Equals(text, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => p.Title.Equals(text, StringComparison.OrdinalIgnoreCase))
               ?? IndustryProfiles.All.FirstOrDefault(p => p.Matches(text))
               ?? (text.Equals("other", StringComparison.OrdinalIgnoreCase) || text.Equals("baqi", StringComparison.OrdinalIgnoreCase) ? IndustryProfiles.General : null);
    }

    /// <summary>
    /// <c>industry</c> shows the choices (taps); <c>industry grocery</c> / <c>business type kiryana</c> sets the business type and answers with that kind of shop's
    /// guide: example product lines, an example order, tips and the data to enter first. Only wording changes, never behaviour.
    /// </summary>
    private async Task HandleIndustryAsync(Seller seller, string? text, CancellationToken ct)
    {
        IndustryProfile? chosen = null;
        if (!string.IsNullOrWhiteSpace(text))
            chosen = IndustryProfiles.All.Append(IndustryProfiles.General).FirstOrDefault(p => p.Key.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? IndustryProfiles.All.FirstOrDefault(p => p.Matches(text!));

        if (chosen is null)
        {
            var current = seller.BusinessType is null ? "" : $"\nAbhi: {seller.BusinessType}";
            var intro = string.IsNullOrWhiteSpace(text)
                ? MessageTemplates.Get("industry.004", current)
                : MessageTemplates.Get("industry.005", text);
            await SendChoicesAsync(seller.WhatsAppPhoneNumber, intro,
                IndustryProfiles.All.Append(IndustryProfiles.General).Select(p => new ChoiceOption(p.Title, $"industry {p.Key}")).ToList(), ct);
            return;
        }

        seller.BusinessType = chosen.Title;
        await _db.SaveChangesAsync(ct);
        var lines = string.Join("\n", chosen.ProductLines);
        var tips = string.Join("\n", chosen.Tips.Select(t => "• " + t));
        await ReplyAsync(seller,
            MessageTemplates.Get("industry.001", chosen.Title, lines, chosen.OrderExample, tips, chosen.DataFocus), ct);
        SuggestNext("catalog", "data check");
    }
}
