using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Buyer questions the seller writes down ("enquiry Kurti blue", "buyer ne pucha Lawn Suit"). The bot never sees a buyer's chat, so the
// numbers are only as complete as what the seller logged. "enquiries [period]" counts the questions per catalog product, next to the
// units sold of that product in the same window, so a product asked about often but not sold stands out.
public partial class ConversationEngine
{
    private const int MaxEnquiryLength = 200;
    private const int EnquiryRowsShown = 10;

    private async Task HandleBuyerEnquiryAsync(Seller seller, string text, CancellationToken ct)
    {
        var note = text.Length > MaxEnquiryLength ? text[..MaxEnquiryLength] : text;
        var named = await ProductsInTextAsync(seller, note, ct);
        var product = named.FirstOrDefault();

        _db.BuyerEnquiries.Add(new BuyerEnquiry
        {
            SellerId = seller.Id,
            ProductId = product?.Id,
            Text = note,
            CreatedAt = RecordTimeUtc()
        });
        await _db.SaveChangesAsync(ct);

        await ReplyAsync(seller, MessageTemplates.Get("enquiries.001", note,
            product is null ? "" : MessageTemplates.Get("enquiries.002", Formatters.ProductLabel(product))), ct);
        SuggestNext("enquiries", "margin");
    }

    private async Task HandleEnquiryReportAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var enquiries = await _db.BuyerEnquiries.AsNoTracking()
            .Where(e => e.SellerId == seller.Id && e.CreatedAt >= start && e.CreatedAt < end)
            .Select(e => new { e.ProductId, e.Text })
            .ToListAsync(ct);
        if (enquiries.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("enquiries.006", label), ct);
            return;
        }

        var productIds = enquiries.Where(e => e.ProductId is not null).Select(e => e.ProductId!.Value).Distinct().ToList();
        var names = productIds.Count == 0 ? new Dictionary<int, string>()
            : (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && productIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name }).ToListAsync(ct)).ToDictionary(p => p.Id, p => p.Name);
        var sold = productIds.Count == 0 ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : (await _db.OrderItems.AsNoTracking()
                .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= start && i.Order.CreatedAt < end
                            && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned
                            && i.ProductId != null && productIds.Contains(i.ProductId.Value))
                .Select(i => new { i.ProductId, i.Quantity }).ToListAsync(ct))
              .GroupBy(i => names.GetValueOrDefault(i.ProductId!.Value, ""), StringComparer.OrdinalIgnoreCase)
              .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.OrdinalIgnoreCase);

        var rows = enquiries.Where(e => e.ProductId is not null)
            .GroupBy(e => names.GetValueOrDefault(e.ProductId!.Value, ""), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Asked: g.Count(), Sold: sold.GetValueOrDefault(g.Key)))
            .OrderByDescending(r => r.Asked).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var unnamed = enquiries.Count(e => e.ProductId is null);

        var lines = rows.Take(EnquiryRowsShown).Select(r =>
            MessageTemplates.Get("enquiries.004", r.Name, r.Asked, r.Sold, r.Asked > 0 && r.Sold == 0 ? MessageTemplates.Get("enquiries.005") : ""));
        var more = rows.Count > EnquiryRowsShown ? $"\n…aur {rows.Count - EnquiryRowsShown} products" : "";
        var unnamedLine = unnamed == 0 ? "" : MessageTemplates.Get("enquiries.008", unnamed);
        var footer = unnamed == 0 ? "" : MessageTemplates.Get("enquiries.007", unnamed);
        await ReplyAsync(seller, MessageTemplates.Get("enquiries.003", label,
            string.Join("\n", lines.Append(unnamedLine).Where(l => l.Length > 0)), more + footer), ct);
        SuggestNext("margin", "stock");
    }
}
