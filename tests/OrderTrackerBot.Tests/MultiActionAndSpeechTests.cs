using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Ai;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// Several requests in one message, editing a saved order's items by speech, and spoken replies.
public class MultiActionAndSpeechTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new(MockBehavior.Loose);
    private readonly Mock<ITurnInterpreter> _turn = new();
    private readonly List<string> _sent = new();
    private readonly AppDbContext _db;
    private readonly Seller _seller;
    private readonly Product _shirt;

    public MultiActionAndSpeechTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _sent.Add(b)).Returns(Task.CompletedTask);
        _db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        _db.Sellers.Add(_seller);
        _db.SaveChanges();
        _shirt = new Product { SellerId = _seller.Id, Name = "Shirt", Price = 1800 };
        _db.Products.Add(_shirt);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFactory.Dispose();
    }

    private ConversationEngine Engine() => new(_db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
        features: new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false },
        turn: _turn.Object);

    private async Task SayAsync(string message, TurnAction understood)
    {
        _turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), message, It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>())).ReturnsAsync(understood);
        await Engine().HandleIncomingMessageAsync(Phone, message, default);
    }

    // ---- parsing: several actions, edit lines -------------------------------------------------------------------------------------

    [Fact]
    public void Parse_reads_every_action_in_order_and_keeps_the_first_on_top()
    {
        const string json = """
        {"actions":[
          {"action":"show_catalog","orders":[],"products":[],"order_id":null,"customer_name":null,"status":null,"period":null,"pending_only":false,
           "customer":null,"field":null,"value":null,"topic":null,"question":null,"edit_lines":[]},
          {"action":"show_summary","orders":[],"products":[],"order_id":null,"customer_name":null,"status":null,"period":"yesterday","pending_only":false,
           "customer":null,"field":null,"value":null,"topic":null,"question":null,"edit_lines":[]}
        ]}
        """;

        var parsed = OpenAiTurnInterpreter.Parse(json);

        Assert.NotNull(parsed);
        Assert.Equal(TurnAction.ShowCatalog, parsed!.Action);
        var second = Assert.Single(parsed.Also);
        Assert.Equal(TurnAction.ShowSummary, second.Action);
        Assert.Equal("yesterday", second.Period);
    }

    [Fact]
    public void Parse_drops_actions_beyond_the_limit()
    {
        var one = """{"action":"show_catalog","orders":[],"products":[],"order_id":null,"customer_name":null,"status":null,"period":null,"pending_only":false,"customer":null,"field":null,"value":null,"topic":null,"question":null,"edit_lines":[]}""";
        var json = "{\"actions\":[" + string.Join(",", Enumerable.Repeat(one, TurnAction.MaxActionsPerMessage + 3)) + "]}";

        var parsed = OpenAiTurnInterpreter.Parse(json);

        Assert.Equal(TurnAction.MaxActionsPerMessage, 1 + parsed!.Also.Count);
    }

    [Fact]
    public void Parse_reads_a_single_action_object_without_a_list()
    {
        var parsed = OpenAiTurnInterpreter.Parse("""{"action":"show_summary","period":"today"}""");

        Assert.Equal(TurnAction.ShowSummary, parsed!.Action);
        Assert.Empty(parsed.Also);
    }

    [Fact]
    public void Parse_reads_order_edit_lines_and_drops_unknown_ops()
    {
        var json = """
        {"action":"edit_order","order_id":3,"customer_name":null,"status":null,"period":null,"pending_only":false,"orders":[],"products":[],
         "customer":null,"field":null,"value":null,"topic":null,"question":null,
         "edit_lines":[{"op":"set_quantity","item":"kurti","quantity":2,"price":null},{"op":"remove","item":"shirt","quantity":null,"price":null},
                       {"op":"bogus","item":"x","quantity":1,"price":null}]}
        """;

        var parsed = OpenAiTurnInterpreter.Parse(json);

        Assert.Equal(TurnAction.EditOrder, parsed!.Action);
        Assert.Equal(2, parsed.OrderEdits.Count);
        Assert.Equal("set_quantity", parsed.OrderEdits[0].Op);
        Assert.Equal(2, parsed.OrderEdits[0].Quantity);
        Assert.Equal("remove", parsed.OrderEdits[1].Op);
    }

    [Fact]
    public void Edit_lines_are_cleared_for_other_actions()
    {
        var json = """{"action":"show_catalog","edit_lines":[{"op":"remove","item":"shirt","quantity":null,"price":null}]}""";

        var parsed = OpenAiTurnInterpreter.Parse(json);

        Assert.Empty(parsed!.OrderEdits);
    }

    [Fact]
    public void Schema_has_an_actions_list_with_the_edit_order_action()
    {
        // The strict schema must describe the list form the parser reads.
        var text = OpenAiTurnInterpreter.BuildSchemaForTests().ToJsonString();
        Assert.Contains("\"actions\"", text);
        Assert.Contains("edit_order", text);
        Assert.Contains("edit_lines", text);
        Assert.True(JsonDocument.Parse(text).RootElement.TryGetProperty("type", out _));
    }

    // ---- item lookup and spoken replies --------------------------------------------------------------------------------------------

    [Fact]
    public void Item_is_found_by_name_with_its_order_number()
    {
        var items = new List<OrderItem>
        {
            new() { ProductNameSnapshot = "Kurti Red", Quantity = 1, UnitPrice = 1000 },
            new() { ProductNameSnapshot = "Shirt", Quantity = 1, UnitPrice = 1800 },
        };

        Assert.Equal(2, ConversationEngine.FindItemIndex(items, "shirt"));
        Assert.Equal(1, ConversationEngine.FindItemIndex(items, "kurti"));
        Assert.Null(ConversationEngine.FindItemIndex(items, "trouser"));
        Assert.Null(ConversationEngine.FindItemIndex(items, null));
    }

    [Fact]
    public void Only_short_plain_replies_are_read_aloud()
    {
        Assert.True(ConversationEngine.IsSpeakable("✅ Order #3 shipped ho gaya."));
        Assert.False(ConversationEngine.IsSpeakable(new string('a', ConversationEngine.MaxSpokenReplyChars + 1)));
        Assert.False(ConversationEngine.IsSpeakable("1\n2\n3\n4\n5"));
        Assert.False(ConversationEngine.IsSpeakable("Link: https://example.com"));
    }

    // ---- engine flows ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_requests_in_one_message_both_run()
    {
        await SayAsync("catalog dikhao aur aaj ka hisaab bhi", new TurnAction
        {
            Action = TurnAction.ShowCatalog,
            Also = { new TurnAction { Action = TurnAction.ShowSummary } },
        });

        Assert.Contains(_sent, s => s.Contains("Shirt"));
        Assert.True(_sent.Count >= 2);
    }

    [Fact]
    public async Task A_request_behind_an_open_question_waits_and_runs_after_the_answer()
    {
        // The price change waits for YES; the catalog request in the same message runs only after that answer.
        await SayAsync("Shirt ka rate 2000 kar do aur catalog dikhao", new TurnAction
        {
            Action = TurnAction.EditProduct,
            Products = { new AiNewProduct { Name = "Shirt", Price = 2000 } },
            Also = { new TurnAction { Action = TurnAction.ShowCatalog } },
        });

        Assert.Equal(1800, (await _db.Products.FirstAsync(p => p.Id == _shirt.Id)).Price);
        Assert.Equal(1, QueuedCount());

        await SayAsync("YES", new TurnAction { Action = TurnAction.HelpOrChat });

        Assert.Equal(2000, (await _db.Products.FirstAsync(p => p.Id == _shirt.Id)).Price);
        Assert.Equal(0, QueuedCount());
    }

    [Fact]
    public async Task Order_item_change_by_speech_waits_for_yes_and_then_changes_the_order()
    {
        var customer = new Customer { SellerId = _seller.Id, Name = "Sara Khan", Phone = "03004445566", Address = "Model Town Lahore" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        var order = new Order
        {
            SellerId = _seller.Id, CustomerId = customer.Id, Status = OrderStatus.Pending, CreatedAt = DateTime.UtcNow,
            Subtotal = 3600, Total = 3600,
            Items = { new OrderItem { ProductId = _shirt.Id, ProductNameSnapshot = "Shirt", UnitPrice = 1800, Quantity = 2 } },
        };
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        await SayAsync($"order {order.Id} mein shirt ki quantity 3 kar do", new TurnAction
        {
            Action = TurnAction.EditOrder,
            OrderId = order.Id,
            OrderEdits = { new AiOrderEdit { Op = "set_quantity", Item = "Shirt", Quantity = 3 } },
        });

        // Nothing changed yet: the seller has to confirm first.
        Assert.Equal(2, (await _db.OrderItems.FirstAsync()).Quantity);
        Assert.Contains(_sent, s => s.Contains("Order #" + order.Id));

        await SayAsync("YES", new TurnAction { Action = TurnAction.HelpOrChat });

        Assert.Equal(3, (await _db.OrderItems.FirstAsync()).Quantity);
    }

    private int QueuedCount() =>
        SessionContextData.FromJson(_db.Sessions.Single(s => s.SellerId == _seller.Id).ContextJson).ModelQueue.Count;
}
