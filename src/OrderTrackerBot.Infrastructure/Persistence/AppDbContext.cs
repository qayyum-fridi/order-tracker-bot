using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Infrastructure.Persistence;

public class AppDbContext : DbContext, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Seller> Sellers => Set<Seller>();
    public DbSet<ConversationSession> Sessions => Set<ConversationSession>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<LoyaltyRule> LoyaltyRules => Set<LoyaltyRule>();
    public DbSet<SellerPaymentMethod> PaymentMethods => Set<SellerPaymentMethod>();
    public DbSet<ActionLog> ActionLogs => Set<ActionLog>();
    public DbSet<MerchantFeedback> MerchantFeedbacks => Set<MerchantFeedback>();
    public DbSet<CustomerFeedback> CustomerFeedbacks => Set<CustomerFeedback>();
    public DbSet<PriceTier> PriceTiers => Set<PriceTier>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignSend> CampaignSends => Set<CampaignSend>();
    public DbSet<MessageLog> MessageLogs => Set<MessageLog>();
    public DbSet<InstagramConnection> InstagramConnections => Set<InstagramConnection>();
    public DbSet<CommentLead> CommentLeads => Set<CommentLead>();
    public DbSet<SupportQuery> SupportQueries => Set<SupportQuery>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Order>())
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Seller>(e =>
        {
            e.HasIndex(s => s.WhatsAppPhoneNumber).IsUnique();
            e.Property(s => s.BusinessName).HasMaxLength(200);
            e.HasOne(s => s.Session).WithOne(cs => cs.Seller!)
                .HasForeignKey<ConversationSession>(cs => cs.SellerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.Property(p => p.Price).HasColumnType("decimal(18,2)");
            e.Property(p => p.UnitQty).HasColumnType("decimal(18,3)");
            e.Property(p => p.UnitType).HasMaxLength(20);
            e.HasOne(p => p.Seller).WithMany(s => s.Products).HasForeignKey(p => p.SellerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.HasOne(c => c.Seller).WithMany(s => s.Customers).HasForeignKey(c => c.SellerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.Property(o => o.Subtotal).HasColumnType("decimal(18,2)");
            e.Property(o => o.DiscountAmount).HasColumnType("decimal(18,2)");
            e.Property(o => o.Total).HasColumnType("decimal(18,2)");
            e.HasOne(o => o.Seller).WithMany(s => s.Orders).HasForeignKey(o => o.SellerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(o => o.Customer).WithMany(c => c.Orders).HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderItem>(e =>
        {
            e.Property(i => i.UnitPrice).HasColumnType("decimal(18,2)");
            e.HasOne(i => i.Order).WithMany(o => o.Items).HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Discount>(e =>
        {
            e.Property(d => d.Value).HasColumnType("decimal(18,2)");
            e.HasOne(d => d.Seller).WithMany(s => s.Discounts).HasForeignKey(d => d.SellerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LoyaltyRule>(e =>
        {
            e.Property(l => l.DiscountPercent).HasColumnType("decimal(5,2)");
            e.HasOne(l => l.Seller).WithMany(s => s.LoyaltyRules).HasForeignKey(l => l.SellerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SellerPaymentMethod>(e =>
        {
            e.HasOne(p => p.Seller).WithMany(s => s.PaymentMethods).HasForeignKey(p => p.SellerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PriceTier>(e =>
        {
            e.Property(t => t.MinQty).HasColumnType("decimal(18,3)");
            e.Property(t => t.PricePerUnit).HasColumnType("decimal(18,2)");
            e.HasOne(t => t.Product).WithMany().HasForeignKey(t => t.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Campaign>(e => e.HasIndex(c => c.SellerId));
        modelBuilder.Entity<CampaignSend>(e =>
            e.HasOne(s => s.Campaign).WithMany(c => c.Sends).HasForeignKey(s => s.CampaignId).OnDelete(DeleteBehavior.Cascade));
        modelBuilder.Entity<MessageLog>(e => e.HasIndex(m => new { m.Phone, m.CreatedAt }));

        modelBuilder.Entity<ActionLog>(e => e.HasIndex(a => new { a.SellerId, a.Undone }));
        modelBuilder.Entity<MerchantFeedback>(e => e.HasIndex(m => m.SellerId));
        modelBuilder.Entity<CustomerFeedback>(e => e.HasIndex(c => c.SellerId));
        modelBuilder.Entity<InstagramConnection>(e =>
        {
            e.HasIndex(c => c.SellerId).IsUnique();
            e.HasIndex(c => c.IgUserId);
        });
        modelBuilder.Entity<CommentLead>(e =>
        {
            e.HasIndex(l => l.IgCommentId).IsUnique();
            e.HasIndex(l => new { l.SellerId, l.Number });
        });
        modelBuilder.Entity<SupportQuery>(e => e.HasIndex(q => new { q.SellerId, q.Number }));
    }
}
