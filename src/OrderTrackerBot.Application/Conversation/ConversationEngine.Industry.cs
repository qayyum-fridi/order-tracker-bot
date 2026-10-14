using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

public sealed partial class ConversationEngine
{
    /// <summary>The wording profile of this seller's kind of shop (their business type); the general one when not set.</summary>
    private static IndustryProfile Industry(Seller seller) => IndustryProfiles.Resolve(seller.BusinessType);

    /// <summary>The kind-of-shop question at setup: one tap list (row ids are the <c>industry &lt;key&gt;</c> commands). A typed answer that names no kind is asked again.</summary>
    private async Task AskIndustryAsync(Seller seller, string? notUnderstood, CancellationToken ct)
    {
        var intro = notUnderstood is null
            ? "🏪 Aapki dukaan kis qism ki hai?\nYeh chunne se baqi saare examples aur guide aap ki dukaan ke mutabiq honge. Neeche list se tap karein."
            : $"🙏 Maaf kijiye, \"{notUnderstood}\" samajh nahi aaya. Barae meherbani neeche list se apni dukaan ki qism chunein (agar koi na mile to \"General dukaan\").";
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
                ? $"🏪 Aapki dukaan kis qism ki hai?{current}\nChunein, to us ke hisaab se examples aur tips milenge."
                : $"🙏 Maaf kijiye, \"{text}\" samajh nahi aaya. Neeche se chunein:";
            await SendChoicesAsync(seller.WhatsAppPhoneNumber, intro,
                IndustryProfiles.All.Append(IndustryProfiles.General).Select(p => new ChoiceOption(p.Title, $"industry {p.Key}")).ToList(), ct);
            return;
        }

        seller.BusinessType = chosen.Title;
        await _db.SaveChangesAsync(ct);
        var lines = string.Join("\n", chosen.ProductLines);
        var tips = string.Join("\n", chosen.Tips.Select(t => "• " + t));
        await ReplyAsync(seller,
            $"✅ Dukaan ki qism: {chosen.Title}\n\n" +
            $"📦 Products ek ek line mein likhein:\n{lines}\n\n" +
            $"🛒 Order likhne ka tareeqa:\n\"{chosen.OrderExample}\"\n\n" +
            $"💡 Tips:\n{tips}\n\n" +
            $"🎯 Sab se pehle yeh likhein: {chosen.DataFocus}.\nKya kuch rah gaya? \"data check\" likhein.", ct);
        SuggestNext("catalog", "data check");
    }
}
