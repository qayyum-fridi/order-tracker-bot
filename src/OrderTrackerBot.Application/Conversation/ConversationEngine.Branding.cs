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

    private async Task HandleBrandingHelpAsync(Seller seller, string kind, CancellationToken ct)
    {
        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        var isSet = kind == "logo" ? branding?.Logo is not null : branding?.Banner is not null;
        var tip = kind == "logo"
            ? "Tip: chhota, square logo jis ka background saaf ho."
            : "Tip: lambi patti jaisi tasveer (taqreeban 3:1, jaise 1200×400) — receipt ke sab se upar aati hai.";
        await ReplyAsync(seller,
            $"🖼️ Receipt {BrandingLabel(kind)}: {(isSet ? "set hai ✅" : "abhi set nahi")}\n\n" +
            $"Set ya tabdeel karne ke liye apni {kind} ki tasveer bhejein aur caption mein \"{kind}\" likhein.\n{tip}\n\n" +
            $"Hatane ke liye: remove {kind}\nDekhne ke liye: receipt", ct);
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
