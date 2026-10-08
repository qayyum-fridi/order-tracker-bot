using System.Diagnostics;
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
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>Runs only with OTB_PERF=1 so the normal `dotnet test` stays fast.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OTB_PERF") != "1")
            Skip = "Set OTB_PERF=1 to run (OTB_PERF_ORDERS=30000 to change size).";
    }
}

/// <summary>
/// Seeds a seller who already has years of history (default 30k orders) into a file-backed Sqlite DB, then drives the
/// real <see cref="ConversationEngine"/> and records latency + correctness. Seed time is the ceiling for any future bulk importer.
/// </summary>
public class LargeAccountPerfTests : IDisposable
{
    private const string Phone = "923001234567";
    private const int ItemsPerOrder = 2;
    private static readonly int OrderCount = int.TryParse(Environment.GetEnvironmentVariable("OTB_PERF_ORDERS"), out var n) ? n : 30_000;
    private static readonly int CustomerCount = Math.Max(1, OrderCount / 10);

    private readonly ITestOutputHelper _out;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"otb-perf-{Guid.NewGuid():N}.db");
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _replies = new();
    private readonly List<string> _aiCalls = new();

    public LargeAccountPerfTests(ITestOutputHelper output)
    {
        _out = output;
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _replies.Add(text))
            .Returns(Task.CompletedTask);
        // Anything the deterministic parser does not recognise reaches the AI; record it (it would cost 1-3s + money in prod) and answer "unclear".
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<AiAnalysisContext, string, CancellationToken>((_, text, _) => _aiCalls.Add(text))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        return db;
    }

    private ConversationEngine NewEngine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>());

    [PerfFact]
    public async Task LargeAccount_ImportSpeed_SearchLatency_And_Accuracy()
    {
        // ---- 1. Bulk import (seed) -------------------------------------------------------------
        int sellerId;
        var now = DateTime.UtcNow;
        var oldestOrderId = 0;
        decimal oldestTotal = 0;
        string oldestCustomer = "";
        int expectedNonCancelled = 0;
        decimal expectedSales = 0;

        var sw = Stopwatch.StartNew();
        await using (var db = NewDb())
        {
            await db.Database.EnsureCreatedAsync();
            var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Perf Shop", OnboardingComplete = true,
                Session = new ConversationSession { State = ConversationState.Idle } };
            db.Sellers.Add(seller);
            await db.SaveChangesAsync();
            sellerId = seller.Id;

            var customers = Enumerable.Range(1, CustomerCount).Select(i => new Customer
            {
                SellerId = sellerId, Name = $"Customer{i:D5} Khan", Phone = $"0300{i:D7}", Address = $"House {i}, Lahore"
            }).ToList();
            db.Customers.AddRange(customers);
            await db.SaveChangesAsync();

            const int batch = 2_000;
            for (var start = 0; start < OrderCount; start += batch)
            {
                for (var i = start; i < Math.Min(start + batch, OrderCount); i++)
                {
                    // order 0 is ~5 years old, the last order is today
                    var created = now.AddDays(-1825.0 * (OrderCount - i) / OrderCount);
                    var status = i % 20 == 0 ? OrderStatus.Cancelled : OrderStatus.Delivered;
                    var items = Enumerable.Range(0, ItemsPerOrder).Select(k => new OrderItem
                    {
                        ProductNameSnapshot = $"Product {(i + k) % 50}", UnitPrice = 1000 + (i % 7) * 100, Quantity = 1 + k
                    }).ToList();
                    var subtotal = items.Sum(x => x.UnitPrice * x.Quantity);
                    db.Orders.Add(new Order
                    {
                        SellerId = sellerId, CustomerId = customers[i % CustomerCount].Id, Status = status,
                        PaymentStatus = PaymentStatus.Paid, Subtotal = subtotal, Total = subtotal, AmountPaid = subtotal,
                        CreatedAt = created, UpdatedAt = created, Items = items
                    });
                    if (status != OrderStatus.Cancelled) { expectedNonCancelled++; expectedSales += subtotal; }
                    if (i == 0) { oldestTotal = subtotal; oldestCustomer = customers[0].Name; }
                }
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
            oldestOrderId = await db.Orders.Where(o => o.SellerId == sellerId).OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
        }
        sw.Stop();
        _out.WriteLine($"IMPORT  {OrderCount} orders ({OrderCount * ItemsPerOrder} items, {CustomerCount} customers): {sw.Elapsed.TotalSeconds:F1}s = {OrderCount / sw.Elapsed.TotalSeconds:F0} orders/s");

        // ---- 2. Search latency + accuracy through the real engine ------------------------------
        // Each command is sent twice on fresh contexts: the 1st call is cold (JIT, plan cache), the 2nd is what a warm server does.
        async Task<(double cold, double warm, string reply)> Ask(string text)
        {
            double Time(out string reply)
            {
                using var db = NewDb();
                _replies.Clear();
                var t = Stopwatch.StartNew();
                NewEngine(db).HandleIncomingMessageAsync(Phone, text, default).GetAwaiter().GetResult();
                t.Stop();
                reply = string.Join("\n", _replies);
                return t.Elapsed.TotalMilliseconds;
            }
            var cold = Time(out _);
            var warm = Time(out var r);
            return (cold, warm, r);
        }

        var results = new List<(string cmd, double cold, double warm, bool ok, string note)>();
        async Task Check(string cmd, Func<string, bool> accurate, string note = "")
        {
            var (cold, warm, reply) = await Ask(cmd);
            if (_aiCalls.Contains(cmd)) note = "!! NOT A DETERMINISTIC COMMAND: went to the AI. " + note;
            results.Add((cmd, cold, warm, accurate(reply), note));
        }

        string Money(decimal v) => v.ToString("N0");

        await Check($"order {oldestOrderId}", r => r.Contains($"Order #{oldestOrderId}") && r.Contains(oldestCustomer) && r.Contains(Money(oldestTotal)), "5-year-old order by id");
        await Check("order 99999999", r => r.Contains("nahi mila"), "missing order");
        await Check($"{oldestCustomer} ka order", r => r.Contains(oldestCustomer), "customer's LATEST order (not the old one)");
        await Check($"customer {oldestCustomer}", r => r.Contains(oldestCustomer), "customer detail by name");
        await Check($"customer Customer{CustomerCount:D5}", r => r.Contains($"Customer{CustomerCount:D5}"), "customer detail, last-created customer");
        await Check("customer list", r => r.Contains($"{CustomerCount} total"), "customer list (page 1)");
        await Check("orders today", r => r.Length > 0, "orders today");
        await Check("today's summary", r => r.Length > 0, "daily summary");
        await Check("profit", r => r.Length > 0, "profit 30 days");

        _out.WriteLine($"{"command",-36} {"cold ms",8} {"warm ms",8}  accurate");
        foreach (var (cmd, cold, warm, ok, note) in results)
            _out.WriteLine($"{cmd,-36} {cold,8:F0} {warm,8:F0}  {(ok ? "OK   " : "WRONG")} {note}");

        // ---- 3. DB-level ground truth (aggregates) ---------------------------------------------
        await using (var db = NewDb())
        {
            var dbCount = await db.Orders.CountAsync(o => o.SellerId == sellerId && o.Status != OrderStatus.Cancelled);
            Assert.Equal(expectedNonCancelled, dbCount);
            var dbSales = (await db.Orders.Where(o => o.SellerId == sellerId && o.Status != OrderStatus.Cancelled).Select(o => o.Total).ToListAsync()).Sum();
            Assert.Equal(expectedSales, dbSales);
        }

        Assert.All(results, r => Assert.True(r.ok, $"'{r.cmd}' returned the wrong answer"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { /* best effort */ }
    }
}
