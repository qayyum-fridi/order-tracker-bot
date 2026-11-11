using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;

namespace OrderTrackerBot.Tests;

// A product that comes in a carton: everything is kept per piece; the carton only converts (cost / pieces, cartons x pieces, carton price / pieces).
public class ProductPackTests : IDisposable
{
    private const string Phone = "923001234567";
    private const string OilMessage = "sarson ka tel hai, carton 1000 ka aata hai, ek carton mein 10 piece, 3 carton hain, piece 140 ka aur carton 1200 ka bechta hun";
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly Mock<IAiOrderAssistant> _ai = new(MockBehavior.Loose);
    private readonly Mock<ITurnInterpreter> _turn = new();
    private readonly List<string> _sent = new();
    private readonly AppDbContext _db;
    private readonly Seller _seller;

    public ProductPackTests()
    {
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => _sent.Add(t)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, b, _, _) => _sent.Add(b)).Returns(Task.CompletedTask);
        _db = _dbFactory.CreateContext();
        _seller = new Seller { WhatsAppPhoneNumber = Phone, BusinessName = "Bismillah Kiryana", OnboardingComplete = true, Session = new ConversationSession { State = ConversationState.Idle } };
        _db.Sellers.Add(_seller);
        _db.Customers.Add(new Customer { Seller = _seller, Name = "Ali Raza", Phone = "03215557788", Address = "Saddar Karachi" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _dbFactory.Dispose();
    }

    private async Task SayAsync(string message, TurnAction? understood = null)
    {
        _sent.Clear();
        if (understood is not null)
            _turn.Setup(t => t.UnderstandAsync(It.IsAny<TurnContext>(), message, It.IsAny<AiImageInput?>(), It.IsAny<CancellationToken>())).ReturnsAsync(understood);
        var engine = new ConversationEngine(_db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>(),
            features: new FeatureOptions { ModelFirst = "on", MvpOnly = true, ShortcutButtons = false, ConfirmButtons = false, SuggestionButtons = false },
            turn: _turn.Object);
        await engine.HandleIncomingMessageAsync(Phone, message, default);
    }

    private static AiNewProduct Oil(decimal? price = 140, decimal? cost = null) => new()
    {
        Name = "Sarson Tel", Price = price, Cost = cost, PackName = "carton", PackQty = 10, PackCost = 1000, PackPrice = 1200, StockPacks = 3,
    };

    private Task AddOilAsync() => SayAsync(OilMessage, new TurnAction { Action = TurnAction.AddProduct, Products = { Oil() } });

    [Fact]
    public void Pack_figures_become_per_piece_figures()
    {
        var (product, derived) = ProductPack.FromModel(Oil(), new HashSet<long> { 1000, 10, 3, 140, 1200 });

        Assert.Equal(140, product.Price);
        Assert.Equal(100, product.Cost);      // 1000 / 10
        Assert.Equal(30, product.Stock);      // 3 x 10
        Assert.Equal("10", product.Attributes[ProductPack.QtyKey]);
        Assert.Equal("1200", product.Attributes[ProductPack.PriceKey]);
        Assert.Equal("carton", product.Attributes[ProductPack.NameKey]);
        Assert.Equal(new decimal[] { 100, 30 }, derived);
    }

    [Fact]
    public void A_cost_the_model_worked_out_itself_is_replaced_by_the_computed_one()
    {
        // the model divided 1000 by 10 on its own and got it wrong; 90 was never said
        var (product, _) = ProductPack.FromModel(Oil(cost: 90), new HashSet<long> { 1000, 10, 3, 140, 1200 });
        Assert.Equal(100, product.Cost);
    }

    [Fact]
    public void Only_a_pack_price_gives_the_piece_price()
    {
        var (product, _) = ProductPack.FromModel(Oil(price: null), new HashSet<long> { 1000, 10, 1200 });
        Assert.Equal(120, product.Price);
        Assert.Null(product.Stock);           // "3" was not said this time
    }

    [Fact]
    public void A_pack_size_the_seller_never_said_is_ignored()
    {
        var (product, derived) = ProductPack.FromModel(Oil(), new HashSet<long> { 140 });
        Assert.Empty(derived);
        Assert.Null(ProductPack.Of(product.Attributes));
    }

    [Theory]
    [InlineData(4, "4")]
    [InlineData(30, "30 (3 carton)")]
    [InlineData(34, "34 (3 carton + 4)")]
    public void Stock_is_shown_with_its_whole_packs(int pieces, string expected) =>
        Assert.Equal(expected, new ProductPack("carton", 10, 1200).StockText(pieces));

    [Fact]
    public async Task Product_said_with_its_carton_is_saved_per_piece()
    {
        await AddOilAsync();

        var oil = await _db.Products.SingleAsync();
        Assert.Equal(140, oil.Price);
        Assert.Equal(100, oil.CostPrice);
        Assert.Equal(30, oil.StockQty);
        var pack = ProductPack.Of(oil);
        Assert.NotNull(pack);
        Assert.Equal(10, pack!.Qty);
        Assert.Equal(1200, pack.Price);
        Assert.Contains("carton = 10 piece", Assert.Single(_sent));
    }

    [Fact]
    public async Task Order_in_cartons_uses_the_carton_rate_and_takes_pieces_from_stock()
    {
        await AddOilAsync();
        await SayAsync("Ali Raza ko 2 carton tel bhej do", new TurnAction
        {
            Action = TurnAction.CreateOrder,
            Orders = { new AiOrderDraft { CustomerName = "Ali Raza", Items = { new AiOrderItemDraft { ProductName = "Sarson Tel", Quantity = 2, InPacks = true } } } },
        });
        var draft = string.Join("\n", _sent);
        Assert.Contains("2,400", draft);   // 2 cartons x 1200
        Assert.Contains("2 carton (20 pc)", draft);
        Assert.Contains("Rs.1,200/carton", draft);
        await SayAsync("yes");

        var item = (await _db.Orders.Include(o => o.Items).SingleAsync()).Items.Single();
        Assert.Equal(20, item.Quantity);
        Assert.Equal(120, item.UnitPrice);
        Assert.Equal(100, item.UnitCost);
        Assert.Equal(10, (await _db.Products.SingleAsync()).StockQty);   // 30 - 20
    }

    [Fact]
    public async Task Order_in_pieces_keeps_the_piece_price()
    {
        await AddOilAsync();
        await SayAsync("Ali Raza ko 3 tel bhej do", new TurnAction
        {
            Action = TurnAction.CreateOrder,
            Orders = { new AiOrderDraft { CustomerName = "Ali Raza", Items = { new AiOrderItemDraft { ProductName = "Sarson Tel", Quantity = 3 } } } },
        });
        await SayAsync("yes");

        var item = (await _db.Orders.Include(o => o.Items).SingleAsync()).Items.Single();
        Assert.Equal(3, item.Quantity);
        Assert.Equal(140, item.UnitPrice);
        Assert.Equal(27, (await _db.Products.SingleAsync()).StockQty);
    }

    [Fact]
    public async Task A_carton_price_said_in_the_order_is_that_orders_rate()
    {
        await AddOilAsync();
        await SayAsync("Ali Raza ko 1 carton tel 1150 ka", new TurnAction
        {
            Action = TurnAction.CreateOrder,
            Orders = { new AiOrderDraft { CustomerName = "Ali Raza", Items = { new AiOrderItemDraft { ProductName = "Sarson Tel", Quantity = 1, InPacks = true, StatedUnitPrice = 1150 } } } },
        });
        await SayAsync("yes");

        var order = await _db.Orders.Include(o => o.Items).SingleAsync();
        Assert.Equal(10, order.Items.Single().Quantity);
        Assert.Equal(115, order.Items.Single().UnitPrice);
        Assert.Equal(1150, order.Total);
        Assert.Equal(140, (await _db.Products.SingleAsync()).Price);   // the listed prices are untouched
    }

    [Fact]
    public async Task Cartons_that_arrive_add_their_pieces_to_stock()
    {
        await AddOilAsync();
        await SayAsync("tel ke 2 carton aur aa gaye", new TurnAction { Action = TurnAction.AddStock, Products = { new AiNewProduct { Name = "Sarson Tel", StockPacks = 2 } } });

        Assert.Equal(50, (await _db.Products.SingleAsync()).StockQty);
        Assert.Contains("50 (5 carton)", Assert.Single(_sent));
    }

    [Fact]
    public async Task Cartons_of_a_product_with_no_carton_size_are_asked_about()
    {
        _db.Products.Add(new Product { SellerId = _seller.Id, Name = "Cheeni", Price = 150, StockQty = 5 });
        await _db.SaveChangesAsync();
        await SayAsync("cheeni ke 2 carton aaye", new TurnAction { Action = TurnAction.AddStock, Products = { new AiNewProduct { Name = "Cheeni", StockPacks = 2 } } });

        Assert.Equal(5, (await _db.Products.SingleAsync()).StockQty);
        Assert.Contains("kitne piece", Assert.Single(_sent));
    }

    [Fact]
    public async Task A_stock_count_the_seller_never_said_is_not_added()
    {
        await AddOilAsync();
        await SayAsync("tel aur aa gaya hai", new TurnAction { Action = TurnAction.AddStock, Products = { new AiNewProduct { Name = "Sarson Tel", StockPacks = 4 } } });

        Assert.Equal(30, (await _db.Products.SingleAsync()).StockQty);
    }

    // Live finding: "tel ke 2 carton aur aa gaye" came back with the catalog price too and was refused as an unsaid amount.
    [Fact]
    public void Stock_action_keeps_only_the_name_and_the_count()
    {
        var action = OrderTrackerBot.Infrastructure.Ai.OpenAiTurnInterpreter.Parse(
            """{"action":"add_stock","products":[{"name":"Sarson Tel","price":140,"cost":100,"stock":null,"pack_name":"carton","pack_qty":10,"pack_cost":null,"pack_price":1200,"stock_packs":2}]}""");

        var product = Assert.Single(action!.Products);
        Assert.Equal("Sarson Tel", product.Name);
        Assert.Equal(2, product.StockPacks);
        Assert.Null(product.Price);
        Assert.Null(product.Cost);
        Assert.Empty(ConversationEngine.UnquotedAmounts(action, "tel ke 2 carton aur aa gaye hain"));
    }
}
