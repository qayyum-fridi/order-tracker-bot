using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>
/// A confirmed order is saved as one transaction: customer, order, items, stock and action log commit together or not at all.
/// A multi-order YES saves every order or none.
/// </summary>
public sealed class SaveTransactionTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FailProductSaveInterceptor _failures = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();

    public SaveTransactionTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).AddInterceptors(_failures).Options);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db, int shirtStock)
    {
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), null, Mock.Of<IWhatsAppMediaClient>());
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "Lahore, Clothing, @ayesha.collections", "Lawn Suit - 3500", "Shirt - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        var shirt = await db.Products.FirstAsync(p => p.Name == "Shirt");
        shirt.StockQty = shirtStock;
        await db.SaveChangesAsync();
        return engine;
    }

    private static AiOrderDraft Draft(string name, string phone) =>
        new() { CustomerName = name, Phone = phone, Items = { new AiOrderItemDraft { ProductName = "Shirt", MatchedCatalogProductName = "Shirt", Quantity = 1 } } };

    private void AiReads(AiOrderDraft first, params AiOrderDraft[] more) =>
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "new_order", IsOrderAttempt = true, Order = first, AdditionalOrders = more.ToList() });

    /// <summary>What is really in the database, read through a fresh context.</summary>
    private async Task<(int Orders, int Customers, int? ShirtStock)> StoredAsync(params string[] customerNames)
    {
        using var db = CreateContext();
        return (await db.Orders.CountAsync(),
                await db.Customers.CountAsync(c => customerNames.Contains(c.Name)),
                (await db.Products.AsNoTracking().FirstAsync(p => p.Name == "Shirt")).StockQty);
    }

    [Fact]
    public async Task ConfirmedOrder_IsSaved_WithItsStockChange()
    {
        using var db = CreateContext();
        var engine = await OnboardAsync(db, shirtStock: 10);
        AiReads(Draft("Sara", "03001112222"));

        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 shirt, 03001112222", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal((1, 1, 9), await StoredAsync("Sara"));
    }

    [Fact]
    public async Task FailureDuringSave_LeavesNoOrderCustomerOrStockChange()
    {
        using var db = CreateContext();
        var engine = await OnboardAsync(db, shirtStock: 10);
        AiReads(Draft("Sara", "03001112222"));
        await engine.HandleIncomingMessageAsync(Phone, "Sara, 1 shirt, 03001112222", default);

        _failures.FailOnProductSave = 1; // the write that carries the stock change fails, after the order row was inserted
        try { await engine.HandleIncomingMessageAsync(Phone, "yes", default); }
        catch (InvalidOperationException) { } // the worker turns this into a system-error reply; here only the stored state matters

        Assert.Equal((0, 0, 10), await StoredAsync("Sara"));
    }

    [Fact]
    public async Task MultiOrder_FailureOnSecondOrder_SavesNeither()
    {
        using var db = CreateContext();
        var engine = await OnboardAsync(db, shirtStock: 10);
        AiReads(Draft("Sara", "03001112222"), Draft("Bilal", "03003334444"));
        await engine.HandleIncomingMessageAsync(Phone, "Sara 1 shirt 03001112222, Bilal 1 shirt 03003334444", default);

        _failures.FailOnProductSave = 2; // the first order's stock write succeeds, the second order's fails
        try { await engine.HandleIncomingMessageAsync(Phone, "yes", default); }
        catch (InvalidOperationException) { }

        Assert.Equal((0, 0, 10), await StoredAsync("Sara", "Bilal"));
    }

    [Fact]
    public async Task MultiOrder_Confirmed_SavesBoth()
    {
        using var db = CreateContext();
        var engine = await OnboardAsync(db, shirtStock: 10);
        AiReads(Draft("Sara", "03001112222"), Draft("Bilal", "03003334444"));

        await engine.HandleIncomingMessageAsync(Phone, "Sara 1 shirt 03001112222, Bilal 1 shirt 03003334444", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Equal((2, 2, 8), await StoredAsync("Sara", "Bilal"));
    }

    /// <summary>Throws on the Nth save that writes a changed product (a stock change). 0 = never.</summary>
    private sealed class FailProductSaveInterceptor : SaveChangesInterceptor
    {
        public int FailOnProductSave { get; set; }
        private int _productSaves;

        private void Check(DbContext? context)
        {
            if (FailOnProductSave == 0 || context is null) return;
            if (context.ChangeTracker.Entries<Product>().Any(e => e.State == EntityState.Modified) && ++_productSaves == FailOnProductSave)
                throw new InvalidOperationException("injected save failure");
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Check(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Check(eventData.Context);
            return ValueTask.FromResult(result);
        }
    }
}
