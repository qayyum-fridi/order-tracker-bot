using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Tests;

public class CustomFieldTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IFounderAlertNotifier> _founderAlerts = new();
    private readonly List<string> _sent = new();
    private readonly List<(string Name, byte[] Bytes)> _files = new();
    private readonly List<List<string>> _buttons = new();
    private readonly List<List<string>> _lists = new();

    public CustomFieldTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, body, _) => _sent.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, buttons, _) => { _sent.Add(body); _buttons.Add(buttons.ToList()); }).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, sections, _) => { _sent.Add(body); _lists.Add(sections.SelectMany(x => x.Rows).Select(r => r.Id).ToList()); }).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendDocumentAsync(Phone, It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], string, string, string?, CancellationToken>((_, bytes, name, _, _, _) => _files.Add((name, bytes))).ReturnsAsync(true);
    }

    public void Dispose() => _dbFactory.Dispose();

    private async Task<ConversationEngine> OnboardAsync(AppDbContext db)
    {
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object,
            exportWriter: new OrderTrackerBot.Infrastructure.Export.ExportXlsxWriter());
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Lawn Suit - 3500", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        var seller = await db.Sellers.FirstAsync();
        var sara = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(sara);
        await db.SaveChangesAsync();
        var order = new Order { SellerId = seller.Id, CustomerId = sara.Id, Subtotal = 1800, Total = 1800 };
        order.Items.Add(new OrderItem { ProductNameSnapshot = "Kurti", UnitPrice = 1800, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        _sent.Clear();
        _buttons.Clear();
        _lists.Clear();
        return engine;
    }

    [Theory]
    [InlineData("add field product Fabric", CommandKind.CustomFieldAdd, "product", "Fabric")]
    [InlineData("add product field Fabric", CommandKind.CustomFieldAdd, "product", "Fabric")]
    [InlineData("new attribute for customers Birthday", CommandKind.CustomFieldAdd, "customer", "Birthday")]
    [InlineData("add custom field order Gift Note", CommandKind.CustomFieldAdd, "order", "Gift Note")]
    [InlineData("remove field customer Birthday", CommandKind.CustomFieldRemove, "customer", "Birthday")]
    [InlineData("delete attribute product Fabric", CommandKind.CustomFieldRemove, "product", "Fabric")]
    public void Parses_FieldAddRemove(string text, CommandKind kind, string entity, string name)
    {
        var cmd = CommandParser.TryParse(text)!;
        Assert.Equal(kind, cmd.Kind);
        Assert.Equal(entity, cmd.Text);
        Assert.Equal(name, cmd.Text2);
    }

    [Fact]
    public void Parses_FieldListShowAndSet()
    {
        Assert.Equal(CommandKind.CustomFieldList, CommandParser.TryParse("fields")!.Kind);
        Assert.Equal(CommandKind.CustomFieldList, CommandParser.TryParse("custom fields")!.Kind);
        var show = CommandParser.TryParse("fields product Kurti")!;
        Assert.Equal((CommandKind.CustomFieldList, "product", "Kurti"), (show.Kind, show.Text, show.Text2));
        var set = CommandParser.TryParse("set product Kurti Fabric = Pure Cotton")!;
        Assert.Equal((CommandKind.CustomFieldSet, "product", "Kurti Fabric", "Pure Cotton"), (set.Kind, set.Text, set.Text2, set.Text3));
        Assert.Equal("12 May", CommandParser.TryParse("set customer Sara Birthday: 12 May")!.Text3);
    }

    [Fact]
    public async Task DefineSetShow_OnProductCustomerAndOrder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var orderId = (await db.Orders.FirstAsync()).Id;

        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field customer Birthday", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field order Gift Note", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field product fabric", default);
        Assert.Contains(_sent, m => m.Contains("pehle se maujood"));

        await engine.HandleIncomingMessageAsync(Phone, "set product kurti Fabric = Cotton", default);
        await engine.HandleIncomingMessageAsync(Phone, "set customer Sara Birthday = 12 May", default);
        await engine.HandleIncomingMessageAsync(Phone, $"set order {orderId} Gift Note = Eid card", default);
        Assert.Contains(_sent, m => m.Contains("Kurti — Fabric: Cotton"));
        Assert.Contains(_sent, m => m.Contains("Sara — Birthday: 12 May"));
        Assert.Contains(_sent, m => m.Contains($"Order #{orderId} — Gift Note: Eid card"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Lawn", default);
        Assert.Contains(_sent, m => m.Contains("Fabric: Lawn (pehle: Cotton)"));
        Assert.Single(await db.CustomFieldValues.Where(v => v.EntityId == (db.Products.First(p => p.Name == "Kurti").Id)).ToListAsync());

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "fields product Kurti", default);
        Assert.Contains(_sent, m => m.Contains("🏷️ Fabric: Lawn"));
        await engine.HandleIncomingMessageAsync(Phone, "customer Sara", default);
        Assert.Contains(_sent, m => m.Contains("🏷️ Birthday: 12 May"));
        await engine.HandleIncomingMessageAsync(Phone, $"order {orderId}", default);
        Assert.Contains(_sent, m => m.Contains("🏷️ Gift Note: Eid card"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "fields", default);
        Assert.Contains(_sent, m => m.Contains("Product: Fabric") && m.Contains("Customer: Birthday") && m.Contains("Order: Gift Note"));
    }

    [Fact]
    public async Task Set_UnknownFieldOrRecord_ExplainsAndSavesNothing()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);
        Assert.Contains(_sent, m => m.Contains("Pehle product field banayein"));

        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Nonexistent Fabric = Cotton", default);
        Assert.Contains(_sent, m => m.Contains("koi product nahi mila"));
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
    }

    [Fact]
    public async Task ClearValue_AndRemoveField_DeleteValues()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Lawn Suit Fabric = Lawn", default);

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = -", default);
        Assert.Single(await db.CustomFieldValues.ToListAsync());

        await engine.HandleIncomingMessageAsync(Phone, "remove field product Fabric", default);
        Assert.Contains(_sent, m => m.Contains("1 values bhi"));
        Assert.Empty(await db.CustomFields.ToListAsync());      // hidden from every query...
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
        Assert.Single(await db.CustomFields.IgnoreQueryFilters().ToListAsync()); // ...but kept so "undo" can restore it
    }

    [Fact]
    public async Task FieldLimits_AreEnforced()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        for (var i = 1; i <= 10; i++) await engine.HandleIncomingMessageAsync(Phone, $"add field product F{i}", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field product F11", default);
        Assert.Contains(_sent, m => m.Contains("10 fields ho chuki"));
        Assert.Equal(10, await db.CustomFields.CountAsync());
    }

    [Fact]
    public async Task Export_AddsCustomFieldColumns()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Notes", default); // collides with nothing on Catalog, but exercises naming
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);

        await engine.HandleIncomingMessageAsync(Phone, "export catalog", default);

        var (_, bytes) = Assert.Single(_files);
        var rows = MiniExcelLibs.MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: true, sheetName: "Catalog").Cast<IDictionary<string, object>>().ToList();
        Assert.Equal("Cotton", rows.Single(r => (string)r["Product"] == "Kurti")["Fabric"]);
        Assert.Null(rows.Single(r => (string)r["Product"] == "Lawn Suit")["Fabric"]);
    }

    [Fact]
    public async Task ResetAccount_DeletesCustomFields()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Empty(await db.CustomFields.ToListAsync());
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
    }

    [Fact]
    public void Parses_ChoiceFieldAndTapForms()
    {
        var add = CommandParser.TryParse("add field product Fabric: Cotton, Lawn, Silk")!;
        Assert.Equal((CommandKind.CustomFieldAdd, "product", "Fabric: Cotton, Lawn, Silk"), (add.Kind, add.Text, add.Text2));
        var pick = CommandParser.TryParse("set product Kurti")!;
        Assert.Equal((CommandKind.CustomFieldSet, "product", "Kurti", (string?)null), (pick.Kind, pick.Text, pick.Text2, pick.Text3));
        Assert.Equal(CommandKind.CustomFieldSet, CommandParser.TryParse("set product Kurti Fabric")!.Kind);
        var option = CommandParser.TryParse("add option product Fabric: Chiffon")!;
        Assert.Equal((CommandKind.CustomFieldOption, "product", "Fabric", "Chiffon"), (option.Kind, option.Text, option.Text2, option.Text3));
    }

    [Fact]
    public async Task ChoiceField_FewOptions_AreButtons_AndTapSavesValue()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric: Cotton, Lawn, Silk", default);
        Assert.Contains(_sent, m => m.Contains("Fabric (Cotton/Lawn/Silk)") && m.Contains("ban gayi"));

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric", default);
        Assert.Equal(new[] { "Cotton", "Lawn", "Silk" }, Assert.Single(_buttons));
        Assert.Equal(OrderTrackerBot.Domain.Enums.ConversationState.AwaitingCustomFieldChoice, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "Lawn", default); // the tapped button's title
        Assert.Contains(_sent, m => m.Contains("Kurti — Fabric: Lawn"));
        Assert.Equal("Lawn", (await db.CustomFieldValues.SingleAsync()).Value);
        Assert.Equal(OrderTrackerBot.Domain.Enums.ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task ChoiceField_ManyOptions_AreAList_AndTypedValueMustMatchAnOption()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field customer Size: S, M, L, XL, XXL", default);

        await engine.HandleIncomingMessageAsync(Phone, "set customer Sara Size = xl", default); // case-insensitive, stored canonically
        Assert.Equal("XL", (await db.CustomFieldValues.SingleAsync()).Value);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "set customer Sara Size = Huge", default);
        Assert.Contains(_sent, m => m.Contains("ka option nahi hai"));
        Assert.Equal(new[] { "S", "M", "L", "XL", "XXL" }, Assert.Single(_lists));

        await engine.HandleIncomingMessageAsync(Phone, "M", default); // the tapped list row id
        Assert.Equal("M", (await db.CustomFieldValues.SingleAsync()).Value);
    }

    [Fact]
    public async Task SetRecordOnly_ListsFields_ThenValues_FullyTappable()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric: Cotton, Lawn", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Season: Summer, Winter", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Care", default); // free text

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti", default);
        Assert.Equal(new[] { "Fabric", "Season", "Care" }, Assert.Single(_buttons));

        await engine.HandleIncomingMessageAsync(Phone, "Season", default);
        Assert.Equal(new[] { "Summer", "Winter" }, _buttons.Last());
        await engine.HandleIncomingMessageAsync(Phone, "2", default); // a number picks by position too
        Assert.Contains(_sent, m => m.Contains("Kurti — Season: Winter"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti", default);
        await engine.HandleIncomingMessageAsync(Phone, "Care", default);
        Assert.Contains(_sent, m => m.Contains("Care likhein"));
        await engine.HandleIncomingMessageAsync(Phone, "Dry clean only", default);
        Assert.Equal("Dry clean only", (await db.CustomFieldValues.Include(v => v.CustomField).SingleAsync(v => v.CustomField!.Name == "Care")).Value);
        Assert.Equal(OrderTrackerBot.Domain.Enums.ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task Picker_CancelAndOtherCommands_LeaveTheFlow()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric: Cotton, Lawn", default);

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "cancel", default);
        Assert.Equal(OrderTrackerBot.Domain.Enums.ConversationState.Idle, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric", default);
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "orders today", default);
        Assert.Equal(OrderTrackerBot.Domain.Enums.ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
        Assert.Contains(_sent, m => m.Contains("Orders") || m.Contains("order"));

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric", default);
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "blah blah", default);
        Assert.Contains(_sent, m => m.Contains("option chunein"));
        Assert.Equal(OrderTrackerBot.Domain.Enums.ConversationState.AwaitingCustomFieldChoice, (await db.Sessions.FirstAsync()).State);
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
    }

    [Fact]
    public async Task OptionLimits_AndAddOption()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric: Cotton", default);
        Assert.Contains(_sent, m => m.Contains("Kam az kam 2 options"));
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric: Cotton, A very very long option name", default);
        Assert.Contains(_sent, m => m.Contains("bohat lambi"));
        Assert.Empty(await db.CustomFields.ToListAsync());

        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric: Cotton, Lawn", default);
        await engine.HandleIncomingMessageAsync(Phone, "add option product Fabric: Silk, lawn", default); // lawn is a duplicate
        Assert.Equal("Cotton|Lawn|Silk", (await db.CustomFields.SingleAsync()).Options);

        await engine.HandleIncomingMessageAsync(Phone, "add field product Care", default);
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "add option product Care: Dry", default);
        Assert.Contains(_sent, m => m.Contains("choice field nahi hai"));

        await engine.HandleIncomingMessageAsync(Phone, "fields", default);
        Assert.Contains(_sent, m => m.Contains("Fabric (Cotton/Lawn/Silk)"));
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Gap fixes: visibility (catalog / receipt / order confirmation), remove option, Urdu words, number+date types, tap-to-start
    // ---------------------------------------------------------------------------------------------------------------------------------

    private ConversationEngine EngineWithReceipt(AppDbContext db, List<OrderTrackerBot.Application.Abstractions.ReceiptData> captured)
    {
        var pdf = new Mock<IReceiptPdfGenerator>();
        pdf.Setup(g => g.Generate(It.IsAny<ReceiptData>())).Callback<ReceiptData>(captured.Add).Returns(new byte[] { 1, 2, 3 });
        return new ConversationEngine(db, _ai.Object, _sender.Object, _founderAlerts.Object, receiptPdf: pdf.Object);
    }

    [Fact]
    public async Task CatalogAndShareCatalog_ShowValues_ButShareHidesPrivateFields()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Cost: number private", default);
        Assert.Contains(_sent, m => m.Contains("🔒 Private"));
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Cost = 900", default);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);
        Assert.Contains(_sent, m => m.Contains("🏷️ Fabric: Cotton · Cost: 900"));

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "share catalog", default);
        Assert.Contains(_sent, m => m.Contains("Fabric: Cotton") && !m.Contains("Cost") && !m.Contains("900"));

        // hide/show toggles it
        await engine.HandleIncomingMessageAsync(Phone, "show field product Cost", default);
        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "share catalog", default);
        Assert.Contains(_sent, m => m.Contains("Cost: 900"));
    }

    [Fact]
    public async Task Receipt_ShowsPublicProductAndOrderFields_NotPrivateOnes()
    {
        using var db = _dbFactory.CreateContext();
        var captured = new List<ReceiptData>();
        var engine = EngineWithReceipt(db, captured);
        foreach (var m in new[] { "start", "Roman Urdu", "Setup shuru karein", "Ayesha Collections", "skip", "10 ke qareeb", "Lawn Suit - 3500", "Kurti - 1800", "done" })
            await engine.HandleIncomingMessageAsync(Phone, m, default);
        var seller = await db.Sellers.FirstAsync();
        var sara = new Customer { SellerId = seller.Id, Name = "Sara", Phone = "03001112222" };
        db.Customers.Add(sara);
        await db.SaveChangesAsync();
        var kurti = await db.Products.FirstAsync(p => p.Name == "Kurti");
        var order = new Order { SellerId = seller.Id, CustomerId = sara.Id, Subtotal = 1800, Total = 1800 };
        order.Items.Add(new OrderItem { ProductId = kurti.Id, ProductNameSnapshot = "Kurti", UnitPrice = 1800, Quantity = 1 });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Cost: number private", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field order Gift Note", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field order Margin private", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Cost = 900", default);
        await engine.HandleIncomingMessageAsync(Phone, $"set order {order.Id} Gift Note = Eid card", default);
        await engine.HandleIncomingMessageAsync(Phone, $"set order {order.Id} Margin = 40%", default);

        await engine.HandleIncomingMessageAsync(Phone, $"receipt {order.Id}", default);

        var receipt = Assert.Single(captured);
        Assert.Equal("Fabric: Cotton", receipt.Lines.Single().Details);
        var field = Assert.Single(receipt.Fields!);
        Assert.Equal(("Gift Note", "Eid card"), (field.Name, field.Value));
    }

    [Fact]
    public async Task SavedOrder_OffersSetFieldsButton_AndItStartsThePicker()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field order Gift: Yes, No", default);
        _buttons.Clear();

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AiMessageAnalysis
        {
            Intent = "new_order", IsOrderAttempt = true,
            Order = new AiOrderDraft { CustomerName = "Bilal", Phone = "03005556666", Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } } }
        });
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 1 kurti, 03005556666", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);
        Assert.Contains(_buttons, b => b.SequenceEqual(new[] { "🏷️ Set fields" }));

        await engine.HandleIncomingMessageAsync(Phone, "🏷️ Set fields", default); // the tapped button
        Assert.Equal(new[] { "Yes", "No" }, _buttons.Last());
        await engine.HandleIncomingMessageAsync(Phone, "Yes", default);
        var orderId = (await db.Orders.OrderBy(o => o.Id).LastAsync()).Id;
        Assert.Contains(_sent, m => m.Contains($"Order #{orderId} — Gift: Yes"));
    }

    [Fact]
    public async Task CustomerProfileAndOrderDetail_OfferSetFieldsButton()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        var orderId = (await db.Orders.FirstAsync()).Id;
        await engine.HandleIncomingMessageAsync(Phone, "add field customer Birthday: date", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field customer Tier: Gold, Silver", default);

        _buttons.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "customer Sara", default);
        Assert.Contains(_buttons, b => b.SequenceEqual(new[] { "🏷️ Set fields" }));

        // no order fields defined -> no button on the order detail
        _buttons.Clear();
        await engine.HandleIncomingMessageAsync(Phone, $"order {orderId}", default);
        Assert.Empty(_buttons);

        await engine.HandleIncomingMessageAsync(Phone, "set fields", default);
        // Sara has 2 customer fields -> field picker; pick Tier, then Silver
        await engine.HandleIncomingMessageAsync(Phone, "customer Sara", default);
        await engine.HandleIncomingMessageAsync(Phone, "🏷️ Set fields", default);
        Assert.Equal(new[] { "Birthday", "Tier" }, _buttons.Last());
        await engine.HandleIncomingMessageAsync(Phone, "Tier", default);
        await engine.HandleIncomingMessageAsync(Phone, "Silver", default);
        Assert.Equal("Silver", (await db.CustomFieldValues.Include(v => v.CustomField).SingleAsync(v => v.CustomField!.Name == "Tier")).Value);
    }

    [Fact]
    public async Task FieldsCommand_WalksEntityRecordFieldValue_WithTaps()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric: Cotton, Lawn", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Season: Summer, Winter", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field customer Tier: Gold, Silver", default);
        _buttons.Clear();
        _lists.Clear();

        await engine.HandleIncomingMessageAsync(Phone, "fields", default);
        Assert.Equal(new[] { "Product", "Customer" }, _buttons.Last());
        Assert.Equal(ConversationState.AwaitingCustomFieldChoice, (await db.Sessions.FirstAsync()).State);

        await engine.HandleIncomingMessageAsync(Phone, "Product", default);
        var kurtiId = (await db.Products.FirstAsync(p => p.Name == "Kurti")).Id;
        Assert.Contains($"r{kurtiId}", _lists.Last());

        await engine.HandleIncomingMessageAsync(Phone, $"r{kurtiId}", default); // the tapped list row
        Assert.Equal(new[] { "Fabric", "Season" }, _buttons.Last());
        await engine.HandleIncomingMessageAsync(Phone, "Season", default);
        await engine.HandleIncomingMessageAsync(Phone, "Winter", default);
        Assert.Contains(_sent, m => m.Contains("Kurti — Season: Winter"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task FieldsCommand_WithNoFields_ExplainsAndStaysIdle()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "fields", default);
        Assert.Contains(_sent, m => m.Contains("Abhi koi custom field nahi"));
        Assert.Equal(ConversationState.Idle, (await db.Sessions.FirstAsync()).State);
    }

    [Fact]
    public async Task RemoveOption_KeepsSavedValues_AndRefusesBelowTwoOptions()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric: Cotton, Lawn, Silk", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Silk", default);

        await engine.HandleIncomingMessageAsync(Phone, "remove option product Fabric: Silk", default);
        Assert.Contains(_sent, m => m.Contains("1 record(s) par purani value rahegi"));
        Assert.Equal("Cotton|Lawn", (await db.CustomFields.SingleAsync()).Options);
        Assert.Equal("Silk", (await db.CustomFieldValues.SingleAsync()).Value);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "remove option product Fabric: Lawn", default);
        Assert.Contains(_sent, m => m.Contains("Kam az kam 2 options"));
        await engine.HandleIncomingMessageAsync(Phone, "remove option product Fabric: Wool", default);
        Assert.Contains(_sent, m => m.Contains("Yeh option nahi mila"));
        Assert.Equal("Cotton|Lawn", (await db.CustomFields.SingleAsync()).Options);
    }

    [Fact]
    public async Task NumberField_ValidatesAndNormalises_AndExportsAsNumber()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Weight: number", default);
        Assert.Contains(_sent, m => m.Contains("Weight (number)"));

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Weight = Rs 1,250.50", default);
        Assert.Equal("1250.5", (await db.CustomFieldValues.SingleAsync()).Value);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Weight = heavy", default);
        Assert.Contains(_sent, m => m.Contains("number likhein"));
        Assert.Equal("1250.5", (await db.CustomFieldValues.SingleAsync()).Value);

        // tapped/typed text stage validates too and keeps asking
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Weight", default);
        Assert.Contains(_sent, m => m.Contains("number, jaise 1500"));
        await engine.HandleIncomingMessageAsync(Phone, "light", default);
        Assert.Equal(ConversationState.AwaitingCustomFieldChoice, (await db.Sessions.FirstAsync()).State);
        await engine.HandleIncomingMessageAsync(Phone, "2", default);
        Assert.Equal("2", (await db.CustomFieldValues.SingleAsync()).Value);

        await engine.HandleIncomingMessageAsync(Phone, "export catalog", default);
        var (_, bytes) = Assert.Single(_files);
        var rows = MiniExcelLibs.MiniExcel.Query(new MemoryStream(bytes), useHeaderRow: true, sheetName: "Catalog").Cast<IDictionary<string, object>>().ToList();
        Assert.Equal(2d, Convert.ToDouble(rows.Single(r => (string)r["Product"] == "Kurti")["Weight"]));
    }

    [Theory]
    [InlineData("12 may", "12 May")]
    [InlineData("12/05/1995", "12 May 1995")]
    [InlineData("3 March 1990", "03 Mar 1990")]
    [InlineData("1995-05-12", "12 May 1995")]
    [InlineData("29 feb", "29 Feb")]
    public async Task DateField_NormalisesDates(string typed, string stored)
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field customer Birthday: date", default);

        await engine.HandleIncomingMessageAsync(Phone, $"set customer Sara Birthday = {typed}", default);

        Assert.Equal(stored, (await db.CustomFieldValues.SingleAsync()).Value);
    }

    [Fact]
    public async Task DateField_RejectsNonDates()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field customer Birthday: date", default);

        await engine.HandleIncomingMessageAsync(Phone, "set customer Sara Birthday = someday", default);

        Assert.Contains(_sent, m => m.Contains("date likhein"));
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
    }

    [Fact]
    public void Parses_UrduScriptCommands()
    {
        var add = CommandParser.TryParse("نئی فیلڈ پروڈکٹ فیبرک")!;
        Assert.Equal((CommandKind.CustomFieldAdd, "product", "فیبرک"), (add.Kind, add.Text, add.Text2));
        var addPostfix = CommandParser.TryParse("فیلڈ شامل کریں کسٹمر سالگرہ: تاریخ")!;
        Assert.Equal((CommandKind.CustomFieldAdd, "customer", "سالگرہ: تاریخ"), (addPostfix.Kind, addPostfix.Text, addPostfix.Text2));
        var remove = CommandParser.TryParse("ہٹائیں فیلڈ آرڈر تحفہ")!;
        Assert.Equal((CommandKind.CustomFieldRemove, "order", "تحفہ"), (remove.Kind, remove.Text, remove.Text2));
        Assert.Equal(CommandKind.CustomFieldList, CommandParser.TryParse("فیلڈز")!.Kind);
        var set = CommandParser.TryParse("سیٹ پروڈکٹ Kurti Fabric = Cotton")!;
        Assert.Equal((CommandKind.CustomFieldSet, "product", "Kurti Fabric", "Cotton"), (set.Kind, set.Text, set.Text2, set.Text3));
        Assert.Equal(CommandKind.CustomFieldSet, CommandParser.TryParse("سیٹ کسٹمر Sara")!.Kind);
        Assert.Equal(CommandKind.CustomFieldPickLast, CommandParser.TryParse("🏷️ فیلڈ سیٹ کریں")!.Kind);
        Assert.Equal(CommandKind.CustomFieldPickLast, CommandParser.TryParse("🏷️ Set fields")!.Kind);
        Assert.Equal(CommandKind.CustomFieldOptionRemove, CommandParser.TryParse("remove option product Fabric: Silk")!.Kind);
        var hide = CommandParser.TryParse("hide field product Cost")!;
        Assert.Equal((CommandKind.CustomFieldVisibility, "product", "Cost", "private"), (hide.Kind, hide.Text, hide.Text2, hide.Text3));
        Assert.Equal("public", CommandParser.TryParse("show field product Cost")!.Text3);
    }

    [Fact]
    public async Task UrduScriptCommands_WorkEndToEnd()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);

        await engine.HandleIncomingMessageAsync(Phone, "نئی فیلڈ پروڈکٹ Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "سیٹ پروڈکٹ Kurti Fabric = Cotton", default);
        Assert.Equal("Cotton", (await db.CustomFieldValues.SingleAsync()).Value);

        await engine.HandleIncomingMessageAsync(Phone, "فیلڈز", default);
        Assert.Contains(_sent, m => m.Contains("Product: Fabric"));
    }

    [Fact]
    public async Task Undo_RestoresPreviousValue_RemovesNewValue_AndBringsBackAClearedOne()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);
        Assert.Contains(_sent, m => m.Contains("Ghalti ho to \"undo\""));
        await engine.HandleIncomingMessageAsync(Phone, "undo", default); // first value -> removed again
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
        Assert.Contains(_sent, m => m.Contains("Reverted") && m.Contains("hata di"));

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Lawn", default);
        await engine.HandleIncomingMessageAsync(Phone, "undo", default); // overwrite -> previous value
        Assert.Equal("Cotton", (await db.CustomFieldValues.SingleAsync()).Value);

        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = -", default);
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
        await engine.HandleIncomingMessageAsync(Phone, "undo", default); // cleared -> back
        Assert.Equal("Cotton", (await db.CustomFieldValues.SingleAsync()).Value);
    }

    [Fact]
    public async Task Undo_AfterTappedChoice_ThenAfterFieldRemoval_UndoesInOrder()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric: Cotton, Lawn", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "Lawn", default);
        Assert.Single(await db.CustomFieldValues.ToListAsync());

        await engine.HandleIncomingMessageAsync(Phone, "remove field product Fabric", default);
        Assert.Empty(await db.CustomFields.ToListAsync());

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "undo", default); // 1st undo: the field is back, with its value
        Assert.Contains(_sent, m => m.Contains("wapas") && m.Contains("1 values ke saath"));
        Assert.Equal("Lawn", (await db.CustomFieldValues.SingleAsync()).Value);

        await engine.HandleIncomingMessageAsync(Phone, "undo", default); // 2nd undo: the earlier value change still works
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
        Assert.Single(await db.CustomFields.ToListAsync());
    }

    [Fact]
    public async Task RemovedField_IsHiddenEverywhere_AndReAddingTheSameNameStartsFresh()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);
        await engine.HandleIncomingMessageAsync(Phone, "remove field product Fabric", default);

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "catalog", default);
        Assert.DoesNotContain(_sent, m => m.Contains("Cotton"));
        await engine.HandleIncomingMessageAsync(Phone, "fields", default);
        Assert.Contains(_sent, m => m.Contains("Abhi koi custom field nahi"));

        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default); // same name: no unique-index clash
        Assert.Single(await db.CustomFields.ToListAsync());
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
        Assert.Single(await db.CustomFields.IgnoreQueryFilters().ToListAsync());

        _sent.Clear();
        await engine.HandleIncomingMessageAsync(Phone, "undo", default); // the older removal can no longer be restored
        Assert.Contains(_sent, m => m.Contains("wapas nahi aa saki"));
        Assert.Single(await db.CustomFields.ToListAsync());
    }

    [Fact]
    public async Task ResetAccount_AlsoDeletesRemovedFields()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);
        await engine.HandleIncomingMessageAsync(Phone, "remove field product Fabric", default);

        await engine.HandleIncomingMessageAsync(Phone, "reset account", default);
        await engine.HandleIncomingMessageAsync(Phone, "yes", default);

        Assert.Empty(await db.CustomFields.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.CustomFieldValues.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task DraftOrderConfirmation_ShowsProductFields_IncludingPrivateOnes()
    {
        using var db = _dbFactory.CreateContext();
        var engine = await OnboardAsync(db);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Fabric", default);
        await engine.HandleIncomingMessageAsync(Phone, "add field product Cost: number private", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Fabric = Cotton", default);
        await engine.HandleIncomingMessageAsync(Phone, "set product Kurti Cost = 900", default);
        _sent.Clear();

        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AiMessageAnalysis
        {
            Intent = "new_order", IsOrderAttempt = true,
            Order = new AiOrderDraft { CustomerName = "Bilal", Phone = "03005556666", Items = { new AiOrderItemDraft { ProductName = "Kurti", MatchedCatalogProductName = "Kurti", Quantity = 1 } } }
        });
        await engine.HandleIncomingMessageAsync(Phone, "Bilal, 1 kurti, 03005556666", default);

        Assert.Contains(_sent, m => m.Contains("Confirm order") && m.Contains("🏷️ Kurti: Fabric: Cotton · Cost: 900"));
    }
}
