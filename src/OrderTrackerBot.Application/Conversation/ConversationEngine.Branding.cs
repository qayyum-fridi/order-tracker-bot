using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

// "logo" / "banner": the seller's own picture on every PDF receipt. Set by sending the image with the caption "logo" or "banner".
public partial class ConversationEngine
{
    private static string BrandingLabel(string kind) => kind == "logo" ? "Logo" : "Banner";

    private async Task SaveBrandingAsync(Seller seller, string kind, byte[] image, CancellationToken ct)
    {
        if (_receiptPdf is null)
        {
            await ReplyAsync(seller, "PDF receipt abhi available nahi hai.", ct);
            return;
        }

        if (!_receiptPdf.CanEmbedImage(image))
        {
            await ReplyAsync(seller, $"⚠️ Yeh tasveer {BrandingLabel(kind)} ke taur par nahi lag sakti — PNG ya JPG tasveer (5 MB se chhoti) dobara bhejein.", ct);
            return;
        }

        var branding = await _db.SellerBrandings.FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        if (branding is null)
        {
            branding = new SellerBranding { SellerId = seller.Id };
            _db.SellerBrandings.Add(branding);
        }
        if (kind == "logo") branding.Logo = image; else branding.Banner = image;
        branding.UpdatedAt = DateTime.UtcNow;

        await ReplyAsync(seller,
            $"✅ {BrandingLabel(kind)} save ho gaya — ab har receipt par nazar aayega.\nDekhne ke liye \"receipt\" likhein. Hatane ke liye \"remove {kind}\".", ct);
    }

    private const string BrandingHow =
        "📸 Kaise karein:\n1) Apni tasveer WhatsApp mein \"Photo/Gallery\" se chunein (Document ke taur par nahi)\n" +
        "2) Caption mein \"logo\" ya \"banner\" likh kar bhej dein\n" +
        "Bas — agli receipt par nazar aayegi.";

    // kind null = the seller asked in general terms ("receipt par image lagani hai"): explain both.
    private async Task HandleBrandingHelpAsync(Seller seller, string? kind, CancellationToken ct)
    {
        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        string State(bool set) => set ? "set hai ✅" : "abhi set nahi";

        if (kind is null)
        {
            await ReplyAsync(seller,
                "🖼️ Aap apni receipt par apna logo aur banner laga saktay hain.\n\n" +
                $"• Logo (chhota, square; naam ke saath): {State(branding?.Logo is not null)}\n" +
                $"• Banner (lambi patti, taqreeban 3:1; receipt ke sab se upar): {State(branding?.Banner is not null)}\n\n" +
                $"{BrandingHow}\n\nHatane ke liye: remove logo / remove banner\nDekhne ke liye: receipt", ct);
            return;
        }

        var tip = kind == "logo"
            ? "Tip: chhota, square logo jis ka background saaf ho."
            : "Tip: lambi patti jaisi tasveer (taqreeban 3:1, jaise 1200×400) — receipt ke sab se upar aati hai.";
        await ReplyAsync(seller,
            $"🖼️ Receipt {BrandingLabel(kind)}: {State(kind == "logo" ? branding?.Logo is not null : branding?.Banner is not null)}\n\n" +
            $"{BrandingHow.Replace("\"logo\" ya \"banner\"", $"\"{kind}\"")}\n{tip}\n\n" +
            $"Hatane ke liye: remove {kind}\nDekhne ke liye: receipt", ct);
    }

    /// <summary>One-time-ish nudge after a seller's first receipts: they may not know logo/banner exist.</summary>
    private async Task<bool> ShouldSuggestBrandingAsync(Seller seller, SellerBranding? branding, CancellationToken ct)
    {
        if (branding?.Logo is not null || branding?.Banner is not null) return false;
        var earlier = await _db.MessageLogs.CountAsync(m => m.Phone == seller.WhatsAppPhoneNumber && m.Direction == "outbound"
            && m.RawText.StartsWith("[document Receipt-"), ct);
        return earlier < 2;
    }

    private async Task HandleRemoveBrandingAsync(Seller seller, string kind, CancellationToken ct)
    {
        var branding = await _db.SellerBrandings.FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        var had = kind == "logo" ? branding?.Logo is not null : branding?.Banner is not null;
        if (branding is null || !had)
        {
            await ReplyAsync(seller, $"Aapka {BrandingLabel(kind)} pehle se set nahi hai.", ct);
            return;
        }

        if (kind == "logo") branding.Logo = null; else branding.Banner = null;
        branding.UpdatedAt = DateTime.UtcNow;
        await ReplyAsync(seller, $"✅ {BrandingLabel(kind)} hata diya gaya.", ct);
    }
}
