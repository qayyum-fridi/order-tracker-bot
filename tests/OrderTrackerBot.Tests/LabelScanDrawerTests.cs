using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using OrderTrackerBot.Infrastructure.Pdf;
using Xunit;

namespace OrderTrackerBot.Tests;

public class LabelScanDrawerParserTests
{
    [Theory]
    [InlineData("label", CommandKind.ShippingLabel)]
    [InlineData("label 12", CommandKind.ShippingLabel)]
    [InlineData("shipping label 12", CommandKind.ShippingLabel)]
    [InlineData("parcel label for Ayesha", CommandKind.ShippingLabel)]
    [InlineData("business label", CommandKind.BusinessLabel)]
    [InlineData("shop sticker", CommandKind.BusinessLabel)]
    [InlineData("label size a6", CommandKind.LabelSize)]
    [InlineData("label size", CommandKind.LabelSize)]
    [InlineData("scan", CommandKind.Scan)]
    [InlineData("scan done", CommandKind.Scan)]
    [InlineData("scan 8961234567890", CommandKind.Scan)]
    [InlineData("barcode", CommandKind.Scan)]
    [InlineData("barcode Lawn Suit 8961234567890", CommandKind.BarcodeSet)]
    [InlineData("barcode remove Lawn Suit", CommandKind.BarcodeSet)]
    [InlineData("drawer", CommandKind.CashDrawer)]
    [InlineData("drawer open 5000", CommandKind.CashDrawer)]
    [InlineData("galla khol 5000", CommandKind.CashDrawer)]
    [InlineData("drawer close 18,500", CommandKind.CashDrawer)]
    [InlineData("cash open 5000", CommandKind.CashDrawer)]
    [InlineData("cash", CommandKind.CashDrawer)]
    [InlineData("cash in 500 udhaar mila", CommandKind.CashMovement)]
    [InlineData("cash out 300 chai", CommandKind.CashMovement)]
    [InlineData("cash sale 1200 kurti", CommandKind.CashMovement)]
    [InlineData("cash undo", CommandKind.CashMovement)]
    public void NewCommands_AreRecognised(string text, CommandKind kind) =>
        Assert.Equal(kind, CommandParser.TryParse(text)?.Kind);

    [Fact]
    public void Fields_AreCarried()
    {
        var label = CommandParser.TryParse("label 12")!;
        Assert.Equal(12, label.Number);

        var byName = CommandParser.TryParse("label for Ayesha")!;
        Assert.Equal("Ayesha", byName.Text);

        var barcode = CommandParser.TryParse("barcode Lawn Suit 8961234567890")!;
        Assert.Equal("Lawn Suit", barcode.Text);
        Assert.Equal("8961234567890", barcode.Text2);

        var close = CommandParser.TryParse("drawer close 18,500 short by chai")!;
        Assert.Equal("close", close.Text);
        Assert.Equal(18500m, close.Amount);
        Assert.Equal("short by chai", close.Text2);

        var open = CommandParser.TryParse("drawer open 5000")!;
        Assert.Equal("open", open.Text);
        Assert.Equal(5000m, open.Amount);

        var cashOut = CommandParser.TryParse("cash out 300 chai")!;
        Assert.Equal("out", cashOut.Text);
        Assert.Equal(300m, cashOut.Amount);
        Assert.Equal("chai", cashOut.Text2);

        Assert.Equal("in", CommandParser.TryParse("cash jama 500")!.Text);
        Assert.Equal("out", CommandParser.TryParse("cash nikala 500")!.Text);
        Assert.Null(CommandParser.TryParse("cash in")!.Amount);
    }

    [Theory]
    [InlineData("cash on delivery orders")]
    [InlineData("labelling machine kharidi")]
    [InlineData("scanning ka kaam")]
    public void OrdinaryWords_AreNotCaught(string text)
    {
        var kind = CommandParser.TryParse(text)?.Kind;
        Assert.True(kind is null || kind is not (CommandKind.ShippingLabel or CommandKind.Scan or CommandKind.CashDrawer or CommandKind.CashMovement or CommandKind.BusinessLabel), $"{text} -> {kind}");
    }

    [Fact]
    public void CashCommands_AreRiskyForVoice() =>
        Assert.True(ActionRisk.RiskyCommands.Contains(CommandKind.CashDrawer) && ActionRisk.RiskyCommands.Contains(CommandKind.CashMovement));

    [Theory]
    [InlineData("4x6", "4x6")]
    [InlineData("4 x 6 inch", "4x6")]
    [InlineData("100x150mm", "4x6")]
    [InlineData("a6", "A6")]
    [InlineData("50x30", "50x30")]
    [InlineData("50*30 mm", "50x30")]
    [InlineData("huge", null)]
    public void NormalizeLabelSize(string text, string? expected) =>
        Assert.Equal(expected, ConversationEngine.NormalizeLabelSize(text));

    [Theory]
    [InlineData("8961234567890", true)]
    [InlineData("KRT-001", true)]
    [InlineData("03001234567", true)]
    [InlineData("12", false)]
    [InlineData("hello", false)]
    [InlineData("two words 123", false)]
    public void LooksLikeScannedCode_NeedsOneTokenWithDigits(string text, bool expected) =>
        Assert.Equal(expected, ConversationEngine.LooksLikeScannedCode(text));
}

public class LabelPdfTests
{
    private static ShippingLabelData Shipping(string size = "4x6", bool cod = true) => new(
        12, "Ali Collections", "923001234567", "Lahore", "Ayesha Khan", "03001112222", "House 5, Street 3, Gulberg, Lahore",
        "2x Lawn Suit, 1x Kurti", cod, 8800, "TCS", "123456789", "Gift wrap", size);

    [Theory]
    [InlineData("4x6")]
    [InlineData("A6")]
    [InlineData("50x30")]
    public void ShippingLabel_IsAPdf_ForEverySize(string size)
    {
        var pdf = new ReceiptPdfGenerator().GenerateShippingLabel(Shipping(size));
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Fact]
    public void ShippingLabel_HandlesUrduNames_AndPrepaid()
    {
        var pdf = new ReceiptPdfGenerator().GenerateShippingLabel(Shipping(cod: false) with { CustomerName = "عائشہ خان", CustomerAddress = "مکان 5، گلبرگ، لاہور", TrackingNumber = null, Notes = null });
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Theory]
    [InlineData("4x6")]
    [InlineData("A6")]
    [InlineData("50x30")]
    public void BusinessLabel_IsAPdf_ForEverySize_AndABadLogoFallsBack(string size)
    {
        var generator = new ReceiptPdfGenerator();
        var plain = new BusinessLabelData("Ali Collections", "923001234567", "Lahore", "@ali.collections", "Shukriya!", null, size);

        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(generator.GenerateBusinessLabel(plain), 0, 5));
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(generator.GenerateBusinessLabel(plain with { Logo = ReceiptPdfTests.TinyPng }), 0, 5));
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(generator.GenerateBusinessLabel(plain with { Logo = new byte[] { 1, 2, 3 } }), 0, 5));
    }
}

public class LabelScanDrawerEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IReceiptPdfGenerator> _pdf = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();
    private ShippingLabelData? _shipping;
    private BusinessLabelData? _business;

    public LabelScanDrawerEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), It.IsAny<string>(), "application/pdf", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _pdf.Setup(p => p.GenerateShippingLabel(It.IsAny<ShippingLabelData>())).Callback<ShippingLabelData>(d => _shipping = d).Returns(new byte[] { 1, 2, 3 });
        _pdf.Setup(p => p.GenerateBusinessLabel(It.IsAny<BusinessLabelData>())).Callback<BusinessLabelData>(d => _business = d).Returns(new byte[] { 4, 5, 6 });
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
    }

    private ConversationEngine Engine(AppDbContext db) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), receiptPdf: _pdf.Object);

    private async Task<(Seller Seller, Order Order)> SeedAsync(AppDbContext db, OrderPaymentMethod method = OrderPaymentMethod.Cod)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", City = "Lahore", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var customer = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222", Address = "House 5, Gulberg", City = "Lahore" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Subtotal = 3500, Total = 3500, PaymentMethod = method };
        order.Items.Add(new OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return (seller, order);
    }

    // ---- labels ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ShippingLabel_CarriesAddressItemsAndTheCodAmount()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, $"label {order.Id}", default);

        Assert.Equal("Sara", _shipping!.CustomerName);
        Assert.Contains("House 5", _shipping.CustomerAddress);
        Assert.Equal("1x Lawn Suit", _shipping.ItemsSummary);
        Assert.True(_shipping.IsCod);
        Assert.Equal(3500m, _shipping.CodAmount);
        Assert.Equal("4x6", _shipping.LabelSize);
        _sender.Verify(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), $"Label-{order.Id}.pdf", "application/pdf", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_sent, m => m.Contains("mark") && m.Contains("shipped"));
    }

    [Fact]
    public async Task ShippingLabel_ForAPaidOrder_IsPrepaid()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db, OrderPaymentMethod.Manual);
        order.PaymentStatus = PaymentStatus.Paid;
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, $"label {order.Id}", default);

        Assert.False(_shipping!.IsCod);
    }

    [Fact]
    public async Task LabelAlone_AsksWhichLabel_InsteadOfGuessing()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "label", default);

        Assert.Null(_shipping);
        Assert.Null(_business);
        Assert.Contains(_sent, m => m.Contains("Kaunsa label") && m.Contains($"label {order.Id}") && m.Contains("business label"));
        Assert.Equal(ConversationState.AwaitingClarificationChoice, (await db.Sessions.AsNoTracking().FirstAsync()).State);
    }

    [Fact]
    public async Task CancelledOrder_GetsNoParcelLabel()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);
        order.Status = OrderStatus.Cancelled;
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, $"label {order.Id}", default);

        Assert.Null(_shipping);
        Assert.Contains(_sent, m => m.Contains("label nahi banta"));
    }

    [Fact]
    public async Task LabelForAnotherSellersOrder_IsNotFound()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);
        db.Sellers.Add(new Seller { WhatsAppPhoneNumber = "923009999999", BusinessName = "Other", OnboardingComplete = true, Session = new ConversationSession() });
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync("923009999999", $"label {order.Id}", default);

        Assert.Null(_shipping);
    }

    [Fact]
    public async Task BusinessLabel_AndLabelSize_Work()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "label size 50x30", default);
        await engine.HandleIncomingMessageAsync(Phone, "business label", default);

        Assert.Equal("Ayesha Collections", _business!.BusinessName);
        Assert.Equal("50x30", _business.LabelSize);
        Assert.Equal("50x30", (await db.Sellers.AsNoTracking().FirstAsync()).LabelSize);

        await engine.HandleIncomingMessageAsync(Phone, "label size 4x6", default);
        Assert.Null((await db.Sellers.AsNoTracking().FirstAsync()).LabelSize);
    }

    // ---- scanner ---------------------------------------------------------------------------------------------------------------------------------

    private async Task<(Product Lawn, Product Kurti)> SeedProductsAsync(AppDbContext db, int sellerId)
    {
        var lawn = new Product { SellerId = sellerId, Name = "Lawn Suit", Price = 3500 };
        var kurti = new Product { SellerId = sellerId, Name = "Kurti", Price = 1800 };
        db.Products.AddRange(lawn, kurti);
        await db.SaveChangesAsync();
        return (lawn, kurti);
    }

    [Fact]
    public async Task Barcode_IsSet_ThenScansFillTheCart_AndScanDoneStartsAnOrder()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, _) = await SeedAsync(db);
        await SeedProductsAsync(db, seller.Id);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "barcode Lawn Suit 8961234567890", default);
        await engine.HandleIncomingMessageAsync(Phone, "barcode Kurti KRT-001", default);
        Assert.Equal("8961234567890", (await db.Products.AsNoTracking().FirstAsync(p => p.Name == "Lawn Suit")).Sku);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "8961234567890", default);
        await engine.HandleIncomingMessageAsync(Phone, "8961234567890", default);
        await engine.HandleIncomingMessageAsync(Phone, "krt-001", default);
        Assert.Contains(_sent, m => m.Contains("cart mein 2") && m.Contains("Lawn Suit"));
        Assert.Contains(_sent, m => m.Contains("3 piece") && m.Contains("8,800"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "scan done", default);

        var session = await db.Sessions.AsNoTracking().FirstAsync();
        var ctx = SessionContextData.FromJson(session.ContextJson);
        Assert.Equal(ConversationState.AwaitingOrderMissingFields, session.State);
        Assert.Equal("CustomerName", ctx.PendingMissingField);
        Assert.Equal(2, ctx.PendingOrder!.Items.Count);
        Assert.Equal(2, ctx.PendingOrder.Items.Single(i => i.ProductName == "Lawn Suit").Quantity);
        Assert.Null(ctx.ScanCart);
    }

    [Fact]
    public async Task ACodeThatIsNotAProductBarcode_IsLeftToTheNormalFlow()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, _) = await SeedAsync(db);
        await SeedProductsAsync(db, seller.Id);

        await Engine(db).HandleIncomingMessageAsync(Phone, "03001234567", default);

        Assert.DoesNotContain(_sent, m => m.Contains("📷"));
        var ctx = SessionContextData.FromJson((await db.Sessions.AsNoTracking().FirstAsync()).ContextJson);
        Assert.Null(ctx.ScanCart);
    }

    [Fact]
    public async Task ScanOfAnUnknownCode_ExplainsHowToLinkIt()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, _) = await SeedAsync(db);
        await SeedProductsAsync(db, seller.Id);

        await Engine(db).HandleIncomingMessageAsync(Phone, "scan 5555555555", default);

        Assert.Contains(_sent, m => m.Contains("nahi mila") && m.Contains("barcode <product ka naam> 5555555555"));
    }

    [Fact]
    public async Task DuplicateBarcode_IsRefused_AndSeveralMatchingProductsAreAsked()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, _) = await SeedAsync(db);
        var (lawn, kurti) = await SeedProductsAsync(db, seller.Id);
        db.Products.Add(new Product { SellerId = seller.Id, Name = "Lawn Suit Blue", Price = 4000 });
        await db.SaveChangesAsync();
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "barcode Kurti 1112223334", default);
        await engine.HandleIncomingMessageAsync(Phone, "barcode Lawn Suit Blue 1112223334", default);
        Assert.Null((await db.Products.AsNoTracking().FirstAsync(p => p.Name == "Lawn Suit Blue")).Sku);
        Assert.Contains(_sent, m => m.Contains("pehle se hai"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "barcode Suit 9998887776", default); // "Suit" fits two products
        Assert.Contains(_sent, m => m.Contains("kai products") || m.Contains("kis ka barcode"));
        Assert.Equal(ConversationState.AwaitingClarificationChoice, (await db.Sessions.AsNoTracking().FirstAsync()).State);
        _ = lawn; _ = kurti;
    }

    [Fact]
    public async Task BarcodeRemove_ClearsIt()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, _) = await SeedAsync(db);
        var (lawn, _) = await SeedProductsAsync(db, seller.Id);
        lawn.Sku = "8961234567890";
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, "barcode remove Lawn Suit", default);

        Assert.Null((await db.Products.AsNoTracking().FirstAsync(p => p.Id == lawn.Id)).Sku);
    }

    // ---- cash drawer -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CashMovements_NeedAnOpenDrawer()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "cash in 500 test", default);

        Assert.Empty(await db.CashMovements.ToListAsync());
        Assert.Contains(_sent, m => m.Contains("Pehle drawer kholein"));
    }

    [Fact]
    public async Task Drawer_OpenCashInOut_CodCollected_ThenCloseBalanced()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, order) = await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "drawer open 5000", default);
        await engine.HandleIncomingMessageAsync(Phone, "cash in 500 udhaar mila", default);
        await engine.HandleIncomingMessageAsync(Phone, "cash out 200 chai", default);

        // a COD order paid after the drawer opened counts as cash
        order.PaymentStatus = PaymentStatus.Paid;
        order.PaidAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "drawer", default);
        Assert.Contains(_sent, m => m.Contains("Ab hona chahiye: Rs.8,800")); // 5000 + 3500 + 500 - 200

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "drawer close 8800", default);
        Assert.Contains(_sent, m => m.Contains("Barabar"));
        var saved = await db.CashDrawerSessions.AsNoTracking().SingleAsync();
        Assert.Equal(8800m, saved.CountedAmount);
        Assert.Equal(8800m, saved.ExpectedAmount);
        Assert.NotNull(saved.ClosedAt);
        _ = seller;
    }

    [Fact]
    public async Task Close_ShortOrOver_SaysHowMuch()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "drawer open 1000", default);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "drawer close 900", default);

        Assert.Contains(_sent, m => m.Contains("Rs.100 KAM"));
        await engine.HandleIncomingMessageAsync(Phone, "drawer open 1000", default);
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "drawer close 1100", default);
        Assert.Contains(_sent, m => m.Contains("Rs.100 ZYADA"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "drawer history", default);
        Assert.Contains(_sent, m => m.Contains("Pichle shift") && m.Contains("kam") && m.Contains("zyada"));
    }

    [Fact]
    public async Task CloseWithoutAnAmount_ShowsTheSummary_AndStaysOpen()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "drawer open 1000", default);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "drawer close", default);

        Assert.Contains(_sent, m => m.Contains("Gine hue paise bhejein"));
        Assert.Null((await db.CashDrawerSessions.AsNoTracking().SingleAsync()).ClosedAt);
    }

    [Fact]
    public async Task OpeningTwice_OrWithoutAnAmount_IsRefused_AndUndoRemovesTheLastEntry()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "drawer open", default);
        Assert.Empty(await db.CashDrawerSessions.ToListAsync());
        Assert.Contains(_sent, m => m.Contains("shuru ke paise"));

        await engine.HandleIncomingMessageAsync(Phone, "drawer open 1000", default);
        await engine.HandleIncomingMessageAsync(Phone, "drawer open 2000", default);
        Assert.Single(await db.CashDrawerSessions.ToListAsync());
        Assert.Contains(_sent, m => m.Contains("pehle se khula"));

        await engine.HandleIncomingMessageAsync(Phone, "cash in 500", default);
        await engine.HandleIncomingMessageAsync(Phone, "cash undo", default);
        Assert.Empty(await db.CashMovements.ToListAsync());
    }

    [Fact]
    public async Task CashOutMoreThanTheDrawerHolds_IsRecorded_WithAWarning()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "drawer open 100", default);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "cash out 500 supplier", default);

        Assert.Contains(_sent, m => m.Contains("Itna cash drawer mein tha hi nahi"));
        Assert.Single(await db.CashMovements.ToListAsync());
    }

    [Fact]
    public async Task ResetAccount_DeletesTheDrawerData_AndLabelSize()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, _) = await SeedAsync(db);
        var engine = Engine(db);
        await engine.HandleIncomingMessageAsync(Phone, "drawer open 1000", default);
        await engine.HandleIncomingMessageAsync(Phone, "cash in 200", default);
        seller.LabelSize = "A6";
        await db.SaveChangesAsync();

        await SellerAccountReset.ResetAsync(db, seller, default);
        await db.SaveChangesAsync();

        Assert.Empty(await db.CashDrawerSessions.ToListAsync());
        Assert.Empty(await db.CashMovements.ToListAsync());
        Assert.Null((await db.Sellers.AsNoTracking().FirstAsync()).LabelSize);
    }

    public void Dispose() => _dbFactory.Dispose();
}
