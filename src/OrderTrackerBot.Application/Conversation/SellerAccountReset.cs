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
        db.Orders.RemoveRange(await db.Orders.Where(o => o.SellerId == seller.Id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        db.Customers.RemoveRange(await db.Customers.Where(c => c.SellerId == seller.Id).ToListAsync(ct));
        db.Products.RemoveRange(await db.Products.Where(p => p.SellerId == seller.Id).ToListAsync(ct));
        db.Discounts.RemoveRange(await db.Discounts.Where(d => d.SellerId == seller.Id).ToListAsync(ct));
        db.LoyaltyRules.RemoveRange(await db.LoyaltyRules.Where(l => l.SellerId == seller.Id).ToListAsync(ct));
        db.PaymentMethods.RemoveRange(await db.PaymentMethods.Where(p => p.SellerId == seller.Id).ToListAsync(ct));
        db.ActionLogs.RemoveRange(await db.ActionLogs.Where(a => a.SellerId == seller.Id).ToListAsync(ct));
        db.MerchantFeedbacks.RemoveRange(await db.MerchantFeedbacks.Where(f => f.SellerId == seller.Id).ToListAsync(ct));
        db.CustomerFeedbacks.RemoveRange(await db.CustomerFeedbacks.Where(f => f.SellerId == seller.Id).ToListAsync(ct));
        db.SellerBrandings.RemoveRange(await db.SellerBrandings.Where(b => b.SellerId == seller.Id).ToListAsync(ct));
        db.Expenses.RemoveRange(await db.Expenses.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.Losses.RemoveRange(await db.Losses.Where(x => x.SellerId == seller.Id).ToListAsync(ct));
        db.WageEntries.RemoveRange(await db.WageEntries.Where(x => x.SellerId == seller.Id).ToListAsync(ct));

        seller.BusinessName = null;
        seller.City = null;
        seller.BusinessType = null;
        seller.InstagramHandle = null;
        seller.OnboardingComplete = false;
        seller.PreferredLanguage = Lang.RomanUrdu;
    }
}
