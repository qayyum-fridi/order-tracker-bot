using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Seller> Sellers { get; }
    DbSet<ConversationSession> Sessions { get; }
    DbSet<Product> Products { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Order> Orders { get; }
    DbSet<OrderItem> OrderItems { get; }
    DbSet<Discount> Discounts { get; }
    DbSet<LoyaltyRule> LoyaltyRules { get; }
    DbSet<SellerPaymentMethod> PaymentMethods { get; }
    DbSet<ActionLog> ActionLogs { get; }
    DbSet<MerchantFeedback> MerchantFeedbacks { get; }
    DbSet<CustomerFeedback> CustomerFeedbacks { get; }
    DbSet<PriceTier> PriceTiers { get; }
    DbSet<Campaign> Campaigns { get; }
    DbSet<CampaignSend> CampaignSends { get; }
    DbSet<MessageLog> MessageLogs { get; }
    DbSet<InstagramConnection> InstagramConnections { get; }
    DbSet<CommentLead> CommentLeads { get; }
    DbSet<SupportQuery> SupportQueries { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
