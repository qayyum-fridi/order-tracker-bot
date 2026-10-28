using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// Deletes a seller's business data and clears their profile so they can set up again. Used by the seller's own
/// "reset account" command and by the admin panel. The seller record, phone number, plan and account status are kept,
/// and so is the message history.
/// </summary>
public static class SellerAccountReset
{
    public static async Task ResetAsync(IAppDbContext db, Seller seller, CancellationToken ct)
    {
        // Everything the seller made or set, so the account starts fresh. Identity and billing (phone, trial, plan, login) stay.
        db.OrderItems.RemoveRange(await db.OrderItems.Where(i => db.Orders.Any(o => o.Id == i.OrderId && o.SellerId == seller.Id)).ToListAsync(ct));
        db.PriceTiers.RemoveRange(await db.PriceTiers.Where(t => db.Products.Any(p => p.Id == t.ProductId && p.SellerId == seller.Id)).ToListAsync(ct));
        db.CampaignSends.RemoveRange(await db.CampaignSends.Where(c => db.Campaigns.Any(k => k.Id == c.CampaignId && k.SellerId == seller.Id)).ToListAsync(ct));
        db.Campaigns.RemoveRange(await db.Campaigns.Where(c => c.SellerId == seller.Id).ToListAsync(ct));
        db.StrategyStats.RemoveRange(await db.StrategyStats.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.CommentLeads.RemoveRange(await db.CommentLeads.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.SupportQueries.RemoveRange(await db.SupportQueries.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.InstagramConnections.RemoveRange(await db.InstagramConnections.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.MessageLogs.RemoveRange(await db.MessageLogs.Where(m => m.Phone == seller.WhatsAppPhoneNumber).ToListAsync(ct));
        db.Orders.RemoveRange(await db.Orders.Where(o => o.SellerId == seller.Id).ToListAsync(ct));
        db.Customers.RemoveRange(await db.Customers.Where(c => c.SellerId == seller.Id).ToListAsync(ct));
        db.CustomerPhotos.RemoveRange(await db.CustomerPhotos.Where(p => p.SellerId == seller.Id).ToListAsync(ct));
        db.ProductImages.RemoveRange(await db.ProductImages.Where(i => i.SellerId == seller.Id).ToListAsync(ct));
        db.Products.RemoveRange(await db.Products.Where(p => p.SellerId == seller.Id).ToListAsync(ct));
        db.Discounts.RemoveRange(await db.Discounts.Where(d => d.SellerId == seller.Id).ToListAsync(ct));
        db.LoyaltyRules.RemoveRange(await db.LoyaltyRules.Where(l => l.SellerId == seller.Id).ToListAsync(ct));
        db.PaymentMethods.RemoveRange(await db.PaymentMethods.Where(p => p.SellerId == seller.Id).ToListAsync(ct));
        db.ActionLogs.RemoveRange(await db.ActionLogs.Where(a => a.SellerId == seller.Id).ToListAsync(ct));
        db.MerchantFeedbacks.RemoveRange(await db.MerchantFeedbacks.Where(f => f.SellerId == seller.Id).ToListAsync(ct));
        db.CustomerFeedbacks.RemoveRange(await db.CustomerFeedbacks.Where(f => f.SellerId == seller.Id).ToListAsync(ct));
        db.SellerBrandings.RemoveRange(await db.SellerBrandings.Where(b => b.SellerId == seller.Id).ToListAsync(ct));
        db.Expenses.RemoveRange(await db.Expenses.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.BuyerEnquiries.RemoveRange(await db.BuyerEnquiries.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.Losses.RemoveRange(await db.Losses.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.WageEntries.RemoveRange(await db.WageEntries.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.Payouts.RemoveRange(await db.Payouts.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.CashMovements.RemoveRange(await db.CashMovements.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.CashDrawerSessions.RemoveRange(await db.CashDrawerSessions.Where(x => x.SellerId == seller.Id).ToListAsync(ct));

        seller.BusinessName = null;
        seller.City = null;
        seller.BusinessType = null;
        seller.InstagramHandle = null;
        seller.Email = null;
        seller.ReceiptSize = null;
        seller.ReceiptFooter = null;
        seller.ReceiptDesignJson = null;
        seller.ReceiptHeader = null;
        seller.ReceiptContact = null;
        seller.LabelSize = null;
        seller.DefaultDeliveryCharge = 0;
        seller.SalesTaxRate = 0;
        seller.Ntn = null;
        seller.Strn = null;
        seller.LastWeeklySummaryAt = null;
        seller.TrialReminderSentAt = null;
        seller.OnboardingComplete = false;
        seller.PreferredLanguage = Lang.RomanUrdu;
    }
}
