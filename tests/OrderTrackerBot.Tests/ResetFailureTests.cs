using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>An AppDbContext whose next SaveChanges can be made to fail, standing in for a database error / crash at the moment of the wipe.</summary>
public sealed class FailingSaveContext : AppDbContext
{
    public FailingSaveContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public bool FailNext { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new DbUpdateException("injected failure");
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}

// Issue #40: a wipe that fails after the YES was claimed must leave every row as it was, must not tell the seller it worked, and the seller can ask again.
public class ResetFailureTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly List<string> _sent = new();

    public ResetFailureTests()
    {
        _connection.Open();
        using var db = Open();
        db.Database.EnsureCreated();
    }

    private static DbContextOptions<AppDbContext> Options(SqliteConnection connection) => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    private AppDbContext Open() => new(Options(_connection));

    private ConversationEngine Engine(AppDbContext db)
    {
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text)).Returns(Task.CompletedTask);
        return new ConversationEngine(db, new Mock<IAiOrderAssistant>().Object, sender.Object, new Mock<IFounderAlertNotifier>().Object);
    }

    private async Task SeedAsync()
    {
        using var db = Open();
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", City = "Lahore", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var customer = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Lawn Suit", Price = 3500 });
        db.Discounts.Add(new Discount { SellerId = seller.Id, Code = "EID10", Type = DiscountType.Percent, Value = 10 });
        await db.SaveChangesAsync();
        var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Subtotal = 3500, Total = 3500 };
        order.Items.Add(new OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
    }

    private async Task<(int Orders, int Customers, int Products, int Discounts, bool Onboarded, string? City, ConversationState State)> SnapshotAsync()
    {
        using var db = Open();
        var seller = await db.Sellers.AsNoTracking().Include(s => s.Session).FirstAsync();
        return (await db.Orders.CountAsync(), await db.Customers.CountAsync(), await db.Products.CountAsync(), await db.Discounts.CountAsync(),
            seller.OnboardingComplete, seller.City, seller.Session!.State);
    }

    [Fact]
    public async Task AFailedWipe_ChangesNothing_SaysNothingFalse_AndCanBeAskedAgain()
    {
        await SeedAsync();

        using (var db = new FailingSaveContext(Options(_connection)))
        {
            var engine = Engine(db);
            await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
            _sent.Clear();

            db.FailNext = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => engine.HandleIncomingMessageAsync(Phone, "yes", default));
        }

        var after = await SnapshotAsync();
        Assert.Equal(1, after.Orders);
        Assert.Equal(1, after.Customers);
        Assert.Equal(1, after.Products);
        Assert.Equal(1, after.Discounts);
        Assert.True(after.Onboarded);
        Assert.Equal("Lahore", after.City);
        Assert.Equal(ConversationState.Idle, after.State);                       // the claim committed, the wipe did not: the seller is simply back to normal
        Assert.DoesNotContain(_sent, m => m.Contains("Account reset ho gaya"));  // never "done" for something that did not happen

        // The seller asks again and it works.
        using (var db = Open())
        {
            var engine = Engine(db);
            await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
            await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        }
        var done = await SnapshotAsync();
        Assert.Equal(0, done.Orders);
        Assert.Equal(0, done.Customers);
        Assert.Equal(0, done.Products);
        Assert.Equal(0, done.Discounts);
        Assert.False(done.Onboarded);
        Assert.Contains(_sent, m => m.Contains("Account reset ho gaya"));
    }

    public void Dispose() => _connection.Dispose();
}
