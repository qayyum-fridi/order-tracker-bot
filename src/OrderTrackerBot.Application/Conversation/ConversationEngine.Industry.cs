using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

public sealed partial class ConversationEngine
{
    /// <summary>The wording profile of this seller's kind of shop (their business type); the general one when not set.</summary>
    private static IndustryProfile Industry(Seller seller) => IndustryProfiles.Resolve(seller.BusinessType);

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
