using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>"Ayesha 2 suit, Bilal 1 kurti": the model returns one draft per buyer (AdditionalOrders). Each buyer keeps only their own items.</summary>
public class MultiBuyerOrderTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();

    public MultiBuyerOrderTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text))
            .Returns(Task.CompletedTask);
    }

    private ConversationEngine CreateEngine(AppDbContext db) => new(db, _ai.Object, _sender.Object, _founderAlerts.Object);

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = CreateEngine(db);
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Kurti - 1800", "Lawn Suit - 2500", "done" })
            await engine.HandleIncomingMessageAsync(Phone, step, default);
        _sent.Clear();
        return engine;
    }

    private void TwoBuyers(string firstProduct, string firstPhone = "03001112222", string secondPhone = "03331234567") =>
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis
            {
                Intent = "new_order", IsOrderAttempt = true,
                Order = new AiOrderDraft
                {
                    CustomerName = "Ayesha", Phone = firstPhone,
                    Items = { new AiOrderItemDraft { ProductName = firstProduct, Quantity = 2 } }
                },
                AdditionalOrders =
                {
                    new AiOrderDraft
                    {
                        CustomerName = "Bilal", Phone = secondPhone,
                        Items = { new AiOrderItemDraft { ProductName = "Kurti", Quantity = 1 } }
                    }
                }
            });

    [Fact]
    public async Task TwoBuyersInOneMessage_BothDraftsAreShownWithTheirOwnItems_AndSavedSeparately()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        TwoBuyers("Lawn Suit");

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha 2 lawn suit, Bilal 1 kurti", default);

        // One line per buyer: each line names only that buyer's items.
        var listing = _sent.Single(m => m.Contains("alag orders")).Split('\n');
        var ayeshaLine = listing.Single(l => l.Contains("Ayesha"));
        var bilalLine = listing.Single(l => l.Contains("Bilal"));
        Assert.Contains("2x Lawn Suit", ayeshaLine);
        Assert.DoesNotContain("Kurti", ayeshaLine);
        Assert.Contains("1x Kurti", bilalLine);
        Assert.DoesNotContain("Lawn Suit", bilalLine);
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var orders = await db.Orders.Include(o => o.Customer).Include(o => o.Items).OrderBy(o => o.Id).ToListAsync();
        Assert.Equal(2, orders.Count);
        Assert.Equal("Ayesha", orders[0].Customer!.Name);
        Assert.Equal(new[] { "Lawn Suit" }, orders[0].Items.Select(i => i.ProductNameSnapshot));
        Assert.Equal(2, orders[0].Items.Single().Quantity);
        Assert.Equal("Bilal", orders[1].Customer!.Name);
        Assert.Equal(new[] { "Kurti" }, orders[1].Items.Select(i => i.ProductNameSnapshot));
        Assert.Equal(1, orders[1].Items.Single().Quantity);
    }

    [Fact]
    public async Task TheFirstBuyersOrderHasAnUnknownProduct_TheSecondBuyersItemsStayWithTheSecond()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        TwoBuyers("Shawl");                                     // unknown product on the FIRST buyer's draft

        await engine.HandleIncomingMessageAsync(Phone, "Ayesha 2 shawl, Bilal 1 kurti", default);
        Assert.Contains(_sent, m => m.Contains("Shawl") && m.Contains("aapke catalog mein nahi mila"));
        _sent.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "1", default);
        await engine.HandleIncomingMessageAsync(Phone, "1200", default);

        // Ayesha's draft is confirmed first; Bilal's draft follows with only his Kurti.
        Assert.Contains(_sent, m => m.Contains("Confirm order") && m.Contains("Ayesha") && m.Contains("Shawl") && !m.Contains("Kurti"));
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Contains(_sent, m => m.Contains("Confirm order") && m.Contains("Bilal") && m.Contains("Kurti") && !m.Contains("Shawl"));
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        var orders = await db.Orders.Include(o => o.Customer).Include(o => o.Items).OrderBy(o => o.Id).ToListAsync();
        Assert.Equal(2, orders.Count);
        Assert.Equal("Ayesha", orders[0].Customer!.Name);
        Assert.Equal(new[] { "Shawl" }, orders[0].Items.Select(i => i.ProductNameSnapshot));
        Assert.Equal("Bilal", orders[1].Customer!.Name);
        Assert.Equal(new[] { "Kurti" }, orders[1].Items.Select(i => i.ProductNameSnapshot));
    }

    public void Dispose() => _dbFactory.Dispose();
}
