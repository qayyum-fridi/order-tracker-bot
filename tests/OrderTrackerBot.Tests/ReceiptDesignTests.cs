using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Pdf;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

public class ReceiptDesignModelTests
{
    [Theory]
    [InlineData("color blue", "#1D4ED8")]
    [InlineData("rang laal", "#B91C1C")]
    [InlineData("colour #aa00bb", "#AA00BB")]
    public void Colour_IsSaved(string message, string expected)
    {
        Assert.True(ReceiptDesign.TryApply(message, new ReceiptDesign(), out var d, out var error));
        Assert.Null(error);
        Assert.Equal(expected, d.Accent);
    }

    [Theory]
    [InlineData("color sparkly")]
    [InlineData("color #12")]
    [InlineData("title ")]
    [InlineData("title 1234567890123456789012345")]
    [InlineData("logo maybe")]
    [InlineData("layout wide")]
    [InlineData("text huge")]
    public void BadValues_AreRefused_AndNothingChanges(string message)
    {
        var start = new ReceiptDesign(Accent: "#111827");
        Assert.True(ReceiptDesign.TryApply(message, start, out var d, out var error));
        Assert.NotNull(error);
        Assert.Equal(start, d);
    }

    [Fact]
    public void OtherText_IsNotAnInstruction()
    {
        Assert.False(ReceiptDesign.TryApply("orders today", new ReceiptDesign(), out _, out _));
        Assert.False(ReceiptDesign.TryApply("mark 3 shipped", new ReceiptDesign(), out _, out _));
    }

    [Fact]
    public void Switches_AndJsonRoundTrip()
    {
        var d = new ReceiptDesign();
        foreach (var m in new[] { "title Invoice", "layout center", "logo off", "phone off", "buyer off", "status off", "text large", "color navy" })
        {
            Assert.True(ReceiptDesign.TryApply(m, d, out d, out var e));
            Assert.Null(e);
        }
        var back = ReceiptDesign.FromJson(d.ToJson());
        Assert.Equal(d, back);
        Assert.True(back.Centered && back.HideLogo && back.HideShopPhone && back.HideBuyerDetails && back.HideOrderStatus);
        Assert.Null(new ReceiptDesign().ToJson());
        Assert.True(ReceiptDesign.TryApply("logo on", back, out var on, out _));
        Assert.False(on.HideLogo);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Accent\":\"javascript\",\"Title\":\"\",\"TextSize\":\"x\"}")]
    [InlineData(null)]
    public void DamagedJson_FallsBackToStandard(string? json) => Assert.True(ReceiptDesign.FromJson(json).IsStandard);

    [Theory]
    [InlineData("A5")]
    [InlineData("A4")]
    [InlineData("80mm")]
    [InlineData("58mm")]
    public void RealPdf_RendersEveryDesign(string paper)
    {
        var generator = new ReceiptPdfGenerator();
        var designs = new[]
        {
            new ReceiptDesign(),
            new ReceiptDesign("#1D4ED8", "Invoice", Centered: true, TextSize: "large"),
            new ReceiptDesign(HideLogo: true, HideShopPhone: true, HideBuyerDetails: true, HideOrderStatus: true, TextSize: "small")
        };
        foreach (var design in designs)
        {
            var data = ReceiptPdfTests.Sample() with { PaperSize = paper, Design = design, Logo = ReceiptPdfTests.TinyPng };
            var pdf = generator.Generate(data).Bytes;
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        }
    }
}

public class ReceiptDesignEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IReceiptPdfGenerator> _pdf = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();
    private readonly List<ReceiptData> _rendered = new();
    private int _documents;

    public ReceiptDesignEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), It.IsAny<string>(), "application/pdf", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _documents++).ReturnsAsync(true);
        _pdf.Setup(p => p.Generate(It.IsAny<ReceiptData>())).Callback<ReceiptData>(d => _rendered.Add(d)).Returns(new ReceiptPdf(new byte[] { 1, 2, 3 }, false));
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private ConversationEngine Engine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), receiptPdf: _pdf.Object);

    private static async Task<Seller> SeedAsync(AppDbContext db, bool withOrder)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        if (withOrder)
        {
            var customer = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Subtotal = 3500, Total = 3500 };
            order.Items.Add(new OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 1 });
            db.Orders.Add(order);
            await db.SaveChangesAsync();
        }
        return seller;
    }

    [Fact]
    public async Task Design_IsLive_Saved_AndUsedByLaterReceipts()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db, withOrder: true);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "receipt design", default);
        Assert.Equal(1, _documents); // opening shows the current look
        await engine.HandleIncomingMessageAsync(Phone, "color blue", default);
        await engine.HandleIncomingMessageAsync(Phone, "title Invoice", default);
        await engine.HandleIncomingMessageAsync(Phone, "layout center", default);
        Assert.Equal(4, _documents); // every change = a fresh preview
        Assert.Equal("#1D4ED8", _rendered[^1].Design!.Accent);
        Assert.Equal("Invoice", _rendered[^1].Design!.Title);
        Assert.True(_rendered[^1].Design!.Centered);

        await engine.HandleIncomingMessageAsync(Phone, "done", default);
        Assert.Contains(_sent, m => m.Contains("Design save ho gaya"));
        Assert.Equal(ConversationState.Idle, (await db.Sellers.AsNoTracking().Include(x => x.Session).Select(x => x.Session!).FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "receipt", default);
        Assert.Equal("#1D4ED8", _rendered[^1].Design!.Accent);
        var saved = await db.Sellers.AsNoTracking().FirstAsync();
        Assert.NotNull(saved.ReceiptDesignJson);
    }

    [Fact]
    public async Task Preview_NeverTakesAReceiptNumber()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db, withOrder: true);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "receipt design", default);
        await engine.HandleIncomingMessageAsync(Phone, "color red", default);
        await engine.HandleIncomingMessageAsync(Phone, "done", default);

        Assert.All(_rendered, r => Assert.Null(r.ReceiptNumber));
        Assert.Null((await db.Orders.AsNoTracking().FirstAsync()).ReceiptNumber);
    }

    [Fact]
    public async Task NoOrders_UsesASample_AndSavesNothingElse()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db, withOrder: false);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "receipt design", default);

        Assert.Equal(1, _documents);
        Assert.Equal("Sara Khan", _rendered[0].CustomerName);
        Assert.Empty(await db.Orders.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Customers.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BadColour_IsRefused_NoPreview_StateKept()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db, withOrder: true);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "receipt design", default);
        await engine.HandleIncomingMessageAsync(Phone, "color sparkly", default);

        Assert.Equal(1, _documents);
        Assert.Contains(_sent, m => m.Contains("rang samajh nahi aaya"));
        var s = await db.Sellers.AsNoTracking().Include(x => x.Session).FirstAsync();
        Assert.Null(s.ReceiptDesignJson);
        Assert.Equal(ConversationState.AwaitingReceiptDesign, s.Session!.State);
    }

    [Fact]
    public async Task SizeFooterAndReset_WorkInsideDesignMode()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db, withOrder: true);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "receipt design", default);
        await engine.HandleIncomingMessageAsync(Phone, "size 80mm", default);
        await engine.HandleIncomingMessageAsync(Phone, "footer Shukriya!", default);
        Assert.Equal("80mm", _rendered[^1].PaperSize);
        Assert.Equal("Shukriya!", _rendered[^1].Footer);

        await engine.HandleIncomingMessageAsync(Phone, "logo off", default);
        Assert.True(_rendered[^1].Design!.HideLogo);
        await engine.HandleIncomingMessageAsync(Phone, "reset", default);
        Assert.True(_rendered[^1].Design!.IsStandard);
        Assert.Null((await db.Sellers.AsNoTracking().FirstAsync()).ReceiptDesignJson);
    }

    [Fact]
    public async Task RealCommand_LeavesDesignMode_AndRuns_DesignStaysSaved()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db, withOrder: true);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "receipt design", default);
        await engine.HandleIncomingMessageAsync(Phone, "color green", default);
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);

        var s = await db.Sellers.AsNoTracking().Include(x => x.Session).FirstAsync();
        Assert.Equal(ConversationState.Idle, s.Session!.State);
        Assert.NotNull(s.ReceiptDesignJson);
    }

    [Fact]
    public async Task Gibberish_ShowsHelp_AndStaysInMode()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db, withOrder: true);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "receipt design", default);
        await engine.HandleIncomingMessageAsync(Phone, "asdf qwer", default);

        Assert.Contains(_sent, m => m.Contains("yeh samajh nahi aaya") && m.Contains("color blue"));
        Assert.Equal(ConversationState.AwaitingReceiptDesign, (await db.Sellers.AsNoTracking().Include(x => x.Session).Select(x => x.Session!).FirstAsync()).State);
    }

    [Fact]
    public async Task ResetAccount_ClearsDesign()
    {
        using var db = _dbFactory.CreateContext();
        var seller = await SeedAsync(db, withOrder: false);
        seller.ReceiptDesignJson = new ReceiptDesign(Accent: "#111827").ToJson();
        await db.SaveChangesAsync();

        await SellerAccountReset.ResetAsync(db, seller, default);
        await db.SaveChangesAsync();

        Assert.Null((await db.Sellers.AsNoTracking().FirstAsync()).ReceiptDesignJson);
    }

    public void Dispose() => _dbFactory.Dispose();
}
