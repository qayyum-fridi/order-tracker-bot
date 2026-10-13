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

public class ReceiptSettingsParserTests
{
    [Theory]
    [InlineData("receipt settings", CommandKind.ReceiptSettings)]
    [InlineData("receipt size 80mm", CommandKind.ReceiptSize)]
    [InlineData("receipt size 58", CommandKind.ReceiptSize)]
    [InlineData("receipt footer Shukriya, dobara tashreef layein", CommandKind.ReceiptFooter)]
    [InlineData("receipt footer off", CommandKind.ReceiptFooter)]
    [InlineData("receipt header Gulberg Branch · Est. 2015", CommandKind.ReceiptHeader)]
    [InlineData("receipt header off", CommandKind.ReceiptHeader)]
    [InlineData("receipt contact 0300-1234567 · ali@shop.com", CommandKind.ReceiptContact)]
    [InlineData("receipt contact off", CommandKind.ReceiptContact)]
    [InlineData("note 12 gift wrap", CommandKind.OrderNote)]
    [InlineData("note 12 off", CommandKind.OrderNote)]
    [InlineData("email ali@shop.com", CommandKind.SetEmail)]
    [InlineData("email off", CommandKind.SetEmail)]
    [InlineData("email receipt", CommandKind.EmailReceipt)]
    [InlineData("email receipt 12", CommandKind.EmailReceipt)]
    [InlineData("email receipt 12 to buyer@mail.com", CommandKind.EmailReceipt)]
    [InlineData("printer", CommandKind.PrinterHelp)]
    [InlineData("printer help", CommandKind.PrinterHelp)]
    public void NewCommands_AreRecognised(string text, CommandKind kind) =>
        Assert.Equal(kind, CommandParser.TryParse(text)?.Kind);

    [Theory]
    [InlineData("receipt")]
    [InlineData("receipt 12")]
    [InlineData("Ayesha ki receipt")]
    public void ExistingReceiptCommands_AreUnchanged(string text) =>
        Assert.Equal(CommandKind.Receipt, CommandParser.TryParse(text)?.Kind);

    [Fact]
    public void Note_And_EmailReceipt_CarryTheirFields()
    {
        var note = CommandParser.TryParse("note 12 gift wrap")!;
        Assert.Equal(12, note.Number);
        Assert.Equal("gift wrap", note.Text);

        var email = CommandParser.TryParse("email receipt 12 to buyer@mail.com")!;
        Assert.Equal(12, email.Number);
        Assert.Equal("buyer@mail.com", email.Text2);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("email me the report")]
    [InlineData("note")]
    public void WithoutAnAddressOrNumber_NothingIsCaught(string text)
    {
        var kind = CommandParser.TryParse(text)?.Kind;
        Assert.True(kind is null || kind is not (CommandKind.SetEmail or CommandKind.EmailReceipt or CommandKind.OrderNote), $"{text} -> {kind}");
    }

    [Theory]
    [InlineData("80mm", "80mm")]
    [InlineData("80", "80mm")]
    [InlineData("3 inch", "80mm")]
    [InlineData("thermal", "80mm")]
    [InlineData("58MM", "58mm")]
    [InlineData("2 inch", "58mm")]
    [InlineData("a5", "A5")]
    [InlineData("A4", "A4")]
    [InlineData("big", null)]
    [InlineData("", null)]
    public void NormalizeReceiptSize_ReadsTheSpellingsSellersUse(string text, string? expected) =>
        Assert.Equal(expected, ConversationEngine.NormalizeReceiptSize(text));
}

public class ReceiptPaperSizePdfTests
{
    private static ReceiptData Sample() => ReceiptPdfTests.Sample();

    [Theory]
    [InlineData("A5")]
    [InlineData("A4")]
    [InlineData("80mm")]
    [InlineData("58mm")]
    public void Generate_ProducesAPdfForEveryPaperSize_WithNoteAndFooter(string size)
    {
        var pdf = new ReceiptPdfGenerator().Generate(Sample() with { PaperSize = size, Notes = "Gift wrap please", Footer = "Shukriya! Wapsi 7 din mein." });
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Theory]
    [InlineData("80mm")]
    [InlineData("58mm")]
    public void Thermal_HandlesUrduNames_LogoPartPaymentAndTax(string size)
    {
        var data = Sample() with
        {
            PaperSize = size, CustomerName = "عائشہ خان", AmountPaid = 2000, Logo = ReceiptPdfTests.TinyPng,
            SalesTaxRate = 18, SalesTaxAmount = 1208, Ntn = "1234567-8", Strn = "17-00-8888-001-37", ReceiptNumber = 7
        };
        var pdf = new ReceiptPdfGenerator().Generate(data);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Theory]
    [InlineData("A5")]
    [InlineData("A4")]
    [InlineData("80mm")]
    [InlineData("58mm")]
    public void Generate_ProducesAPdfForEveryPaperSize_WithHeaderAndContact(string size)
    {
        var pdf = new ReceiptPdfGenerator().Generate(Sample() with
        {
            PaperSize = size, Header = "Gulberg Branch · Est. 2015", Contact = "0300-1234567 · ali@shop.com · Gulberg, Lahore", Logo = ReceiptPdfTests.TinyPng
        });
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    [Fact]
    public void Thermal_ManyItems_StillOnePdf()
    {
        var lines = Enumerable.Range(1, 60).Select(i => new ReceiptLine($"Item {i} with a fairly long product name", i % 3 + 1, 150 + i, (i % 3 + 1) * (150 + i))).ToList();
        var pdf = new ReceiptPdfGenerator().Generate(Sample() with { PaperSize = "58mm", Lines = lines });
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }
}

public class ReceiptSettingsEngineTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IReceiptPdfGenerator> _pdf = new();
    private readonly Mock<IMerchantEmailSender> _email = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly List<string> _sent = new();
    private ReceiptData? _captured;

    public ReceiptSettingsEngineTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _sent.Add(text)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), It.IsAny<string>(), "application/pdf", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _pdf.Setup(p => p.Generate(It.IsAny<ReceiptData>())).Callback<ReceiptData>(d => _captured = d).Returns(new byte[] { 1, 2, 3 });
        // Anything the parser does not recognise reaches the AI; answer "unclear" like the real assistant does when it cannot help.
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
        _email.SetupGet(e => e.IsConfigured).Returns(true);
        _email.Setup(e => e.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<byte[]?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private ConversationEngine Engine(AppDbContext db, bool withEmail = true) =>
        new(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(), receiptPdf: _pdf.Object, emailSender: withEmail ? _email.Object : null);

    private async Task<(Seller Seller, Order Order)> SeedAsync(AppDbContext db)
    {
        var seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collections", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var customer = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Subtotal = 3500, Total = 3500 };
        order.Items.Add(new OrderItem { ProductNameSnapshot = "Lawn Suit", UnitPrice = 3500, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return (seller, order);
    }

    [Fact]
    public async Task SizeFooterAndNote_ReachTheReceipt()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "receipt size 80mm", default);
        await engine.HandleIncomingMessageAsync(Phone, "receipt footer Shukriya!", default);
        await engine.HandleIncomingMessageAsync(Phone, $"note {order.Id} gift wrap", default);
        await engine.HandleIncomingMessageAsync(Phone, $"receipt {order.Id}", default);

        Assert.Equal("80mm", _captured!.PaperSize);
        Assert.Equal("Shukriya!", _captured.Footer);
        Assert.Equal("gift wrap", _captured.Notes);
        var seller = await db.Sellers.AsNoTracking().FirstAsync();
        Assert.Equal("80mm", seller.ReceiptSize);
        Assert.Contains(_sent, m => m.Contains("printer"));
    }

    [Fact]
    public async Task DefaultReceipt_StillA5_AndSettingsCanBeCleared()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, $"receipt {order.Id}", default);
        Assert.Equal("A5", _captured!.PaperSize);
        Assert.Null(_captured.Footer);

        await engine.HandleIncomingMessageAsync(Phone, "receipt size 58mm", default);
        await engine.HandleIncomingMessageAsync(Phone, "receipt size A5", default);
        await engine.HandleIncomingMessageAsync(Phone, "receipt footer off", default);
        Assert.Null((await db.Sellers.AsNoTracking().FirstAsync()).ReceiptSize);
    }

    [Fact]
    public async Task HeaderAndContact_ReachTheReceipt_AndCanBeCleared()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "receipt header Gulberg Branch · Est. 2015", default);
        await engine.HandleIncomingMessageAsync(Phone, "receipt contact 0300-1234567\nali@shop.com", default);
        await engine.HandleIncomingMessageAsync(Phone, $"receipt {order.Id}", default);

        Assert.Equal("Gulberg Branch · Est. 2015", _captured!.Header);
        Assert.Equal("0300-1234567 · ali@shop.com", _captured.Contact);
        var seller = await db.Sellers.AsNoTracking().FirstAsync();
        Assert.Equal("0300-1234567 · ali@shop.com", seller.ReceiptContact);

        await engine.HandleIncomingMessageAsync(Phone, "receipt header off", default);
        await engine.HandleIncomingMessageAsync(Phone, "receipt contact off", default);
        seller = await db.Sellers.AsNoTracking().FirstAsync();
        Assert.Null(seller.ReceiptHeader);
        Assert.Null(seller.ReceiptContact);
    }

    [Fact]
    public async Task OverlongHeader_IsRefused_AndNothingChanges()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "receipt header " + new string('x', 81), default);

        Assert.Null((await db.Sellers.AsNoTracking().FirstAsync()).ReceiptHeader);
        Assert.Contains(_sent, m => m.Contains("80 huroof"));
    }

    [Fact]
    public async Task ReceiptSettings_ShowsLogoBannerHeaderAndContact()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, _) = await SeedAsync(db);
        db.SellerBrandings.Add(new SellerBranding { SellerId = seller.Id, Logo = ReceiptPdfTests.TinyPng });
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync(Phone, "receipt settings", default);

        Assert.Contains(_sent, m => m.Contains("Logo: set") && m.Contains("Banner: abhi nahi") && m.Contains("Header: nahi") && m.Contains("receipt contact"));
    }

    [Fact]
    public async Task UnknownSize_IsRefused_AndNothingChanges()
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, "receipt size huge", default);

        Assert.Null((await db.Sellers.AsNoTracking().FirstAsync()).ReceiptSize);
        Assert.Contains(_sent, m => m.Contains("size samajh nahi aaya"));
    }

    [Fact]
    public async Task Note_OnAnotherSellersOrder_IsNotFound()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);
        var other = new Seller { WhatsAppPhoneNumber = "923009999999", BusinessName = "Other", OnboardingComplete = true, Session = new ConversationSession() };
        db.Sellers.Add(other);
        await db.SaveChangesAsync();

        await Engine(db).HandleIncomingMessageAsync("923009999999", $"note {order.Id} hello", default);

        Assert.Null((await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id)).Notes);
    }

    [Fact]
    public async Task EmailReceipt_GoesToTheSellersOwnAddress_WithThePdf()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);
        var engine = Engine(db);

        await engine.HandleIncomingMessageAsync(Phone, "email Ali@Shop.com", default);
        await engine.HandleIncomingMessageAsync(Phone, $"email receipt {order.Id}", default);

        _email.Verify(e => e.SendAsync("ali@shop.com", It.Is<string>(s => s.Contains("Receipt No. 1")), It.Is<string>(b => b.Contains("Ayesha Collections")),
            $"Receipt-{order.Id}.pdf", It.Is<byte[]?>(b => b != null && b.Length == 3), "application/pdf", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(_sent, m => m.Contains("ali@shop.com") && m.Contains("bhej di"));
    }

    [Fact]
    public async Task EmailReceipt_ToAnExplicitAddress_UsesThatAddress()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, $"email receipt {order.Id} to buyer@mail.com", default);

        _email.Verify(e => e.SendAsync("buyer@mail.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<byte[]?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmailReceipt_WithoutAnyAddress_AsksForOne_AndSendsNothing()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, $"email receipt {order.Id}", default);

        _email.Verify(e => e.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<byte[]?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(_sent, m => m.Contains("email ali@shop.com"));
    }

    [Fact]
    public async Task EmailReceipt_WhenServiceIsOff_SaysSo()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);

        await Engine(db, withEmail: false).HandleIncomingMessageAsync(Phone, $"email receipt {order.Id} to buyer@mail.com", default);

        Assert.Contains(_sent, m => m.Contains("service abhi on nahi"));
    }

    [Fact]
    public async Task FailedEmail_DoesNotBurnTheReceiptNumber()
    {
        using var db = _dbFactory.CreateContext();
        var (_, order) = await SeedAsync(db);
        _email.Setup(e => e.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<byte[]?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Engine(db).HandleIncomingMessageAsync(Phone, $"email receipt {order.Id} to buyer@mail.com", default);

        Assert.Null((await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id)).ReceiptNumber);
        Assert.Contains(_sent, m => m.Contains("Email bhej nahi saka"));
    }

    [Theory]
    [InlineData("email not-an-address")]
    [InlineData("email a@b")]
    public async Task BadEmail_IsNotSaved(string text)
    {
        using var db = _dbFactory.CreateContext();
        await SeedAsync(db);

        await Engine(db).HandleIncomingMessageAsync(Phone, text, default);

        Assert.Null((await db.Sellers.AsNoTracking().FirstAsync()).Email);
    }

    [Fact]
    public async Task ResetAccount_ClearsEmailAndReceiptSettings()
    {
        using var db = _dbFactory.CreateContext();
        var (seller, _) = await SeedAsync(db);
        seller.Email = "ali@shop.com";
        seller.ReceiptSize = "80mm";
        seller.ReceiptFooter = "Shukriya";
        seller.ReceiptHeader = "Gulberg Branch";
        seller.ReceiptContact = "0300-1234567";
        await db.SaveChangesAsync();

        await SellerAccountReset.ResetAsync(db, seller, default);
        await db.SaveChangesAsync();

        var saved = await db.Sellers.AsNoTracking().FirstAsync();
        Assert.Null(saved.Email);
        Assert.Null(saved.ReceiptSize);
        Assert.Null(saved.ReceiptFooter);
        Assert.Null(saved.ReceiptHeader);
        Assert.Null(saved.ReceiptContact);
    }

    public void Dispose() => _dbFactory.Dispose();
}
