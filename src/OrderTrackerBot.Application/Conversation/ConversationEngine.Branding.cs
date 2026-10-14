using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// "logo" / "banner": the seller's own picture on every PDF receipt. Set by sending the image with the caption "logo" or "banner".
public partial class ConversationEngine
{
    private static string BrandingLabel(string kind) => kind == "logo" ? "Logo" : "Banner";

    private async Task SaveBrandingAsync(Seller seller, string kind, byte[] image, CancellationToken ct)
    {
        if (_receiptPdf is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("branding.001"), ct);
            return;
        }

        if (!_receiptPdf.CanEmbedImage(image))
        {
            await ReplyAsync(seller, MessageTemplates.Get("branding.002", BrandingLabel(kind)), ct);
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
            MessageTemplates.Get("branding.003", BrandingLabel(kind), kind), ct);
    }

    private static readonly string BrandingHow =
        MessageTemplates.Get("branding.008");

    // kind null = the seller asked in general terms ("receipt par image lagani hai"): explain both.
    private async Task HandleBrandingHelpAsync(Seller seller, string? kind, CancellationToken ct)
    {
        var branding = await _db.SellerBrandings.AsNoTracking().FirstOrDefaultAsync(b => b.SellerId == seller.Id, ct);
        string State(bool set) => set ? "set hai ✅" : "abhi set nahi";

        if (kind is null)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("branding.004", State(branding?.Logo is not null), State(branding?.Banner is not null), BrandingHow), ct);
            return;
        }

        var tip = kind == "logo"
            ? MessageTemplates.Get("branding.009")
            : MessageTemplates.Get("branding.010");
        await ReplyAsync(seller,
            MessageTemplates.Get("branding.005", BrandingLabel(kind), State(kind == "logo" ? branding?.Logo is not null : branding?.Banner is not null), BrandingHow.Replace("\"logo\" ya \"banner\"", $"\"{kind}\""), tip, kind), ct);
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
            await ReplyAsync(seller, MessageTemplates.Get("branding.006", BrandingLabel(kind)), ct);
            return;
        }

        if (kind == "logo") branding.Logo = null; else branding.Banner = null;
        branding.UpdatedAt = DateTime.UtcNow;
        await ReplyAsync(seller, MessageTemplates.Get("branding.007", BrandingLabel(kind)), ct);
    }
}
