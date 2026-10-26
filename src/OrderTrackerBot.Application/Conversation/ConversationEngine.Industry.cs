using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

public sealed partial class ConversationEngine
{
    /// <summary>The wording profile of this seller's kind of shop (their business type); the general one when not set.</summary>
    private static IndustryProfile Industry(Seller seller) => IndustryProfiles.Resolve(seller.BusinessType);

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
