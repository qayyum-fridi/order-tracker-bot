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
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<CustomerPhoto> CustomerPhotos => Set<CustomerPhoto>();
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
    public DbSet<IssueRecord> IssueRecords => Set<IssueRecord>();
    public DbSet<ProcessedWebhookMessage> ProcessedWebhookMessages => Set<ProcessedWebhookMessage>();
    public DbSet<PendingWebhookMessage> PendingWebhookMessages => Set<PendingWebhookMessage>();
    public DbSet<InstagramConnection> InstagramConnections => Set<InstagramConnection>();
    public DbSet<CommentLead> CommentLeads => Set<CommentLead>();
    public DbSet<SupportQuery> SupportQueries => Set<SupportQuery>();
    public DbSet<SellerBranding> SellerBrandings => Set<SellerBranding>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<CounterReturn> CounterReturns => Set<CounterReturn>();
    public DbSet<BuyerEnquiry> BuyerEnquiries => Set<BuyerEnquiry>();
    public DbSet<StrategyStat> StrategyStats => Set<StrategyStat>();
    public DbSet<Loss> Losses => Set<Loss>();
    public DbSet<WageEntry> WageEntries => Set<WageEntry>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<CashDrawerSession> CashDrawerSessions => Set<CashDrawerSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<UnplacedUtterance> UnplacedUtterances => Set<UnplacedUtterance>();
    public DbSet<UnknownNumberWord> UnknownNumberWords => Set<UnknownNumberWord>();
    public DbSet<LearnedKeyword> LearnedKeywords => Set<LearnedKeyword>();
    public DbSet<KeywordRule> KeywordRules => Set<KeywordRule>();
    public DbSet<MessageTemplateRow> MessageTemplateRows => Set<MessageTemplateRow>();
    public DbSet<FeatureAreaRow> FeatureAreas => Set<FeatureAreaRow>();
    public DbSet<LearnedKeywordVote> LearnedKeywordVotes => Set<LearnedKeywordVote>();
    public DbSet<RouterDecision> RouterDecisions => Set<RouterDecision>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Order>())
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Every money column gets an explicit SQL Server precision (18,2), so none is left to the provider default (EF warning: silent truncation).</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnplacedUtterance>(e => e.HasIndex(u => u.CreatedAt));
        modelBuilder.Entity<UnknownNumberWord>(e => { e.HasIndex(w => w.Word).IsUnique(); e.Property(w => w.Word).HasMaxLength(40); });
        modelBuilder.Entity<RouterDecision>(e =>
        {
            e.HasIndex(d => d.CreatedAt);
            e.Property(d => d.IntentId).HasMaxLength(80);
            e.Property(d => d.Outcome).HasMaxLength(20);
        });
        modelBuilder.Entity<LearnedKeywordVote>(e =>
        {
            e.HasIndex(v => new { v.Word, v.IntentId, v.SellerId }).IsUnique();
            e.Property(v => v.Word).HasMaxLength(40);
            e.Property(v => v.IntentId).HasMaxLength(80);
        });
        modelBuilder.Entity<FeatureAreaRow>(e =>
        {
            e.HasIndex(a => a.Question).IsUnique();
            e.Property(a => a.Question).HasMaxLength(200);
            e.Property(a => a.Pattern).HasMaxLength(2000);
            e.Property(a => a.Options).HasMaxLength(2000);
            e.Property(a => a.SeedHash).HasMaxLength(64);
        });
        modelBuilder.Entity<MessageTemplateRow>(e =>
        {
            e.HasIndex(t => new { t.Language, t.Key }).IsUnique();
            e.Property(t => t.Language).HasMaxLength(20);
            e.Property(t => t.Key).HasMaxLength(120);
            e.Property(t => t.SeedHash).HasMaxLength(64);
        });
        modelBuilder.Entity<KeywordRule>(e =>
        {
            e.HasIndex(k => new { k.Word, k.IntentId }).IsUnique();
            e.Property(k => k.Word).HasMaxLength(40);
            e.Property(k => k.IntentId).HasMaxLength(80);
        });
        modelBuilder.Entity<LearnedKeyword>(e =>
        {
            e.HasIndex(k => new { k.Word, k.IntentId }).IsUnique();
            e.Property(k => k.Word).HasMaxLength(40);
            e.Property(k => k.IntentId).HasMaxLength(80);
        });
        modelBuilder.Entity<Seller>(e =>
        {
            e.HasIndex(s => s.WhatsAppPhoneNumber).IsUnique();
            e.HasIndex(s => s.ClientApiTokenHash); // client dashboard auth lookup
            e.Property(s => s.BusinessName).HasMaxLength(200);
            e.Property(s => s.DefaultDeliveryCharge).HasColumnType("decimal(18,2)");
            e.Property(s => s.SalesTaxRate).HasColumnType("decimal(5,2)");
            e.Property(s => s.Ntn).HasMaxLength(30);
            e.Property(s => s.Strn).HasMaxLength(30);
            e.HasOne(s => s.Session).WithOne(cs => cs.Seller!)
                .HasForeignKey<ConversationSession>(cs => cs.SellerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.Property(p => p.Price).HasColumnType("decimal(18,2)");
            e.Property(p => p.CostPrice).HasColumnType("decimal(18,2)");
            e.Property(p => p.UnitQty).HasColumnType("decimal(18,3)");
            e.Property(p => p.UnitType).HasMaxLength(20);
            e.HasOne(p => p.Seller).WithMany(s => s.Products).HasForeignKey(p => p.SellerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.HasIndex(c => new { c.SellerId, c.Name }); // customer list / name lookup per seller
            e.HasOne(c => c.Seller).WithMany(s => s.Customers).HasForeignKey(c => c.SellerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.Property(o => o.Subtotal).HasColumnType("decimal(18,2)");
            e.Property(o => o.DiscountAmount).HasColumnType("decimal(18,2)");
            e.Property(o => o.DeliveryCharge).HasColumnType("decimal(18,2)");
            e.Property(o => o.AmountPaid).HasColumnType("decimal(18,2)");
            e.Property(o => o.Total).HasColumnType("decimal(18,2)");
            e.Property(o => o.SalesTaxRate).HasColumnType("decimal(5,2)");
            e.Property(o => o.TaxWithheld).HasColumnType("decimal(18,2)");
            e.Property(o => o.CourierFee).HasColumnType("decimal(18,2)");
            e.HasIndex(o => new { o.SellerId, o.ReceiptNumber }).IsUnique();
            e.HasIndex(o => new { o.SellerId, o.CreatedAt }); // date-range reports (today, weekly, export, profit)
            e.HasIndex(o => new { o.SellerId, o.Status });    // status filters (pending/shipped/unpaid lists, counts)
            e.HasOne(o => o.Seller).WithMany(s => s.Orders).HasForeignKey(o => o.SellerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(o => o.Customer).WithMany(c => c.Orders).HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderItem>(e =>
        {
            e.Property(i => i.UnitPrice).HasColumnType("decimal(18,2)");
            e.Property(i => i.UnitCost).HasColumnType("decimal(18,2)");
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
        modelBuilder.Entity<IssueRecord>(e => e.HasIndex(i => new { i.Severity, i.CreatedAt })); // errors/issues lists and daily counts
        modelBuilder.Entity<PendingWebhookMessage>(e =>
        {
            e.HasKey(m => m.MessageId);
            e.Property(m => m.MessageId).HasMaxLength(200);
            e.Property(m => m.Sender).HasMaxLength(50);
        });
        modelBuilder.Entity<ProcessedWebhookMessage>(e =>
        {
            e.HasKey(m => m.MessageId);
            e.Property(m => m.MessageId).HasMaxLength(200);
            e.HasIndex(m => m.ProcessedAt);
        });

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
        modelBuilder.Entity<SellerBranding>(e => e.HasIndex(b => b.SellerId).IsUnique());
        modelBuilder.Entity<CustomerPhoto>(e => e.HasIndex(p => new { p.SellerId, p.CustomerId }).IsUnique());
        modelBuilder.Entity<ProductImage>(e =>
        {
            e.HasIndex(i => new { i.SellerId, i.ProductId });
            e.HasOne(i => i.Product).WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<Expense>(e => e.HasIndex(x => new { x.SellerId, x.CreatedAt }));
        modelBuilder.Entity<Loss>(e => e.HasIndex(x => new { x.SellerId, x.CreatedAt }));
        modelBuilder.Entity<WageEntry>(e => e.HasIndex(x => new { x.SellerId, x.CreatedAt }));
        modelBuilder.Entity<CashDrawerSession>(e => e.HasIndex(x => new { x.SellerId, x.OpenedAt }));
        modelBuilder.Entity<Payout>(e => e.HasIndex(x => new { x.SellerId, x.ReceivedAt }));
        modelBuilder.Entity<StrategyStat>(e => e.HasIndex(x => new { x.SellerId, x.Context, x.Arm }).IsUnique());
        modelBuilder.Entity<CashMovement>(e =>
        {
            e.HasIndex(x => new { x.SellerId, x.CreatedAt });
            e.HasOne(x => x.Session).WithMany(x => x.Movements).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
