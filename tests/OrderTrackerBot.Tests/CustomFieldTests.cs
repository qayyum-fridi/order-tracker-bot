using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
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
        Assert.Contains(_sent, m => m.Contains("1 values bhi delete"));
        Assert.Empty(await db.CustomFields.ToListAsync());
        Assert.Empty(await db.CustomFieldValues.ToListAsync());
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
        Assert.Contains(_sent, m => m.Contains("options: Cotton, Lawn, Silk"));

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
        Assert.Contains(_sent, m => m.Contains("free-text field hai"));

        await engine.HandleIncomingMessageAsync(Phone, "fields", default);
        Assert.Contains(_sent, m => m.Contains("Fabric (Cotton/Lawn/Silk)"));
    }
}
