using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Stage 4: every lookup is scoped to one seller; exact and partial matches stay apart; several matches are never picked.
public class ReferenceResolverTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly ReferenceResolver _resolver;
    private int _sellerA;
    private int _sellerB;

    public ReferenceResolverTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _resolver = new ReferenceResolver(_db);

        var a = new Seller { WhatsAppPhoneNumber = "923001111111" };
        var b = new Seller { WhatsAppPhoneNumber = "923002222222" };
        _db.Sellers.AddRange(a, b);
        _db.SaveChanges();
        _sellerA = a.Id;
        _sellerB = b.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void AddCustomer(int sellerId, string name) =>
        _db.Customers.Add(new Customer { SellerId = sellerId, Name = name });

    private void AddProduct(int sellerId, string name, bool active = true) =>
        _db.Products.Add(new Product { SellerId = sellerId, Name = name, Price = 100, IsActive = active });

    [Fact]
    public async Task Customer_ExactMatch_ResolvesEvenWhenPartialNamesExist()
    {
        AddCustomer(_sellerA, "Ali");
        AddCustomer(_sellerA, "Ali Raza");
        _db.SaveChanges();

        var result = await _resolver.ResolveCustomerAsync(_sellerA, "Ali", default);

        Assert.Equal(ReferenceStatus.Resolved, result.Status);
        Assert.Equal(MatchKind.Exact, result.Kind);
    }

    [Fact]
    public async Task Customer_TwoExactMatches_AreMultiple_NotFirstPick()
    {
        AddCustomer(_sellerA, "Ali");
        AddCustomer(_sellerA, "ali");
        _db.SaveChanges();

        var result = await _resolver.ResolveCustomerAsync(_sellerA, "Ali", default);

        Assert.Equal(ReferenceStatus.Multiple, result.Status);
        Assert.Null(result.Id);
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public async Task Customer_PartialSingleMatch_IsMarkedPartial()
    {
        AddCustomer(_sellerA, "Ali Raza");
        _db.SaveChanges();

        var result = await _resolver.ResolveCustomerAsync(_sellerA, "Raza", default);

        Assert.Equal(ReferenceStatus.Resolved, result.Status);
        Assert.Equal(MatchKind.Partial, result.Kind);
    }

    [Fact]
    public async Task Customer_OtherSellersCustomer_IsNotFound()
    {
        AddCustomer(_sellerB, "Sara");
        _db.SaveChanges();

        var byName = await _resolver.ResolveCustomerAsync(_sellerA, "Sara", default);
        Assert.Equal(ReferenceStatus.NotFound, byName.Status);
    }

    [Fact]
    public async Task Customer_ById_OwnedByOtherSeller_IsNotFound()
    {
        AddCustomer(_sellerB, "Sara");
        _db.SaveChanges();
        var sara = await _db.Customers.SingleAsync();

        Assert.Equal(ReferenceStatus.NotFound, (await _resolver.ResolveCustomerByIdAsync(_sellerA, sara.Id, default)).Status);
        Assert.Equal(ReferenceStatus.Resolved, (await _resolver.ResolveCustomerByIdAsync(_sellerB, sara.Id, default)).Status);
    }

    [Fact]
    public async Task Customer_DeletedRecord_IsNotFound()
    {
        AddCustomer(_sellerA, "Sara");
        _db.SaveChanges();
        var sara = await _db.Customers.SingleAsync();
        sara.DeletedAt = DateTime.UtcNow;
        _db.SaveChanges();

        Assert.Equal(ReferenceStatus.NotFound, (await _resolver.ResolveCustomerByIdAsync(_sellerA, sara.Id, default)).Status);
    }

    [Fact]
    public async Task Order_NeedsOwnSellerAndOwnCustomer()
    {
        AddCustomer(_sellerA, "Sara");
        AddCustomer(_sellerB, "Bilal");
        _db.SaveChanges();
        var sara = await _db.Customers.SingleAsync(c => c.Name == "Sara");
        var bilal = await _db.Customers.SingleAsync(c => c.Name == "Bilal");
        _db.Orders.Add(new Order { SellerId = _sellerA, CustomerId = sara.Id });
        _db.Orders.Add(new Order { SellerId = _sellerA, CustomerId = bilal.Id });   // A's order, but the customer belongs to B
        _db.SaveChanges();
        var ok = await _db.Orders.SingleAsync(o => o.CustomerId == sara.Id);
        var mixed = await _db.Orders.SingleAsync(o => o.CustomerId == bilal.Id);

        Assert.Equal(ReferenceStatus.Resolved, (await _resolver.ResolveOrderAsync(_sellerA, ok.Id, default)).Status);
        Assert.Equal(ReferenceStatus.NotFound, (await _resolver.ResolveOrderAsync(_sellerA, mixed.Id, default)).Status);
        Assert.Equal(ReferenceStatus.NotFound, (await _resolver.ResolveOrderAsync(_sellerB, ok.Id, default)).Status);
    }

    [Fact]
    public async Task Product_ByName_RespectsSellerAndActiveFlag()
    {
        AddProduct(_sellerA, "Shirt");
        AddProduct(_sellerA, "Old Shirt", active: false);
        AddProduct(_sellerB, "Shirt Pro");
        _db.SaveChanges();

        var exact = await _resolver.ResolveProductAsync(_sellerA, "Shirt", default);
        Assert.Equal(ReferenceStatus.Resolved, exact.Status);
        Assert.Equal(MatchKind.Exact, exact.Kind);

        var partial = await _resolver.ResolveProductAsync(_sellerA, "shirt", default);
        Assert.Equal(MatchKind.Exact, partial.Kind);

        var none = await _resolver.ResolveProductAsync(_sellerA, "Old", default);
        Assert.Equal(ReferenceStatus.NotFound, none.Status);
    }

    [Fact]
    public async Task Product_ById_OtherSellerOrInactive_IsNotFound()
    {
        AddProduct(_sellerB, "Mug");
        AddProduct(_sellerA, "Cup", active: false);
        _db.SaveChanges();
        var mug = await _db.Products.SingleAsync(p => p.Name == "Mug");
        var cup = await _db.Products.SingleAsync(p => p.Name == "Cup");

        Assert.Equal(ReferenceStatus.NotFound, (await _resolver.ResolveProductByIdAsync(_sellerA, mug.Id, default)).Status);
        Assert.Equal(ReferenceStatus.NotFound, (await _resolver.ResolveProductByIdAsync(_sellerA, cup.Id, default)).Status);
    }
}
