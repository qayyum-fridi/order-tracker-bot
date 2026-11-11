using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Measures the ONE understanding call (Features:ModelFirst) against the REAL model on a fixed shop: free-form sentences about customers, orders and the
/// catalog in Roman Urdu, Urdu script and English, with the words in any order, the way a seller types or a voice note is transcribed. No command list.
/// Skipped unless OTB_TURN_EVAL=1 and OPENAI_API_KEY are set:
/// <c>OTB_TURN_EVAL=1 OPENAI_API_KEY=... dotnet test --filter TurnUnderstandingEval --logger "console;verbosity=detailed"</c>
/// (OTB_TURN_EVAL_MODEL=gpt-4o to try another model). It starts from text, so speech-to-text mistakes and the handlers are not measured here.
/// Add every real sentence that went wrong to <see cref="Cases"/>; the run fails below <see cref="PassRate"/>.
/// </summary>
public class TurnUnderstandingEval
{
    private const double PassRate = 0.95;
    private readonly ITestOutputHelper _output;
    public TurnUnderstandingEval(ITestOutputHelper output) => _output = output;

    internal sealed record Case(string Message, string[] Actions, Func<TurnAction, bool>? Check = null, bool Voice = false);

    private static bool Has(string? value, string part) => value is not null && value.Contains(part, StringComparison.OrdinalIgnoreCase);
    private static bool Item(TurnAction a, string name, int quantity) =>
        a.Orders.Count > 0 && a.Orders[0].Items.Any(i => Has(i.ProductName, name) && i.Quantity == quantity);
    private static bool Buyer(TurnAction a, params string[] names) => a.Orders.Count > 0 && names.Any(n => Has(a.Orders[0].CustomerName, n));
    private static bool Product(TurnAction a, decimal price, params string[] names) =>
        a.Products.Count > 0 && a.Products[0].Price == price && (names.Length == 0 || names.Any(n => Has(a.Products[0].Name, n)));
    private static bool Pack(TurnAction a, int qty, decimal cost, decimal? price, int? stockPacks) =>
        a.Products.Count == 1 && a.Products[0].PackQty == qty && a.Products[0].PackCost == cost && a.Products[0].PackPrice == price && a.Products[0].StockPacks == stockPacks;
    private static bool Packs(TurnAction a, int count) =>
        a.Orders.Count > 0 && a.Orders[0].Items.Count == 1 && a.Orders[0].Items[0].InPacks && a.Orders[0].Items[0].Quantity == count;
    private static bool Named(TurnAction a, params string[] names) => names.Any(n => Has(a.CustomerName, n));

    private const string Order = TurnAction.CreateOrder, Status = TurnAction.UpdateStatus, HowTo = TurnAction.HowTo, Clarify = TurnAction.Clarify;

    internal static readonly Case[] Cases =
    {
        // how-to questions: never run, never a support request (the live failures)
        new("میں نے نئی پراڈکٹ ایڈ کرنی ہے وہ کیسے کر سکتا ہوں؟", new[] { HowTo }, a => a.Topic == TurnAction.AddProduct, Voice: true),
        new("میں پراڈکٹ میں کیسے ایڈ کر سکتا؟", new[] { HowTo }, a => a.Topic == TurnAction.AddProduct, Voice: true),
        new("product kaise add karun", new[] { HowTo }, a => a.Topic == TurnAction.AddProduct),
        new("customer kaise add karte hain", new[] { HowTo }, a => a.Topic == TurnAction.AddCustomer),
        new("order likhne ka tarika kya hai", new[] { HowTo }, a => a.Topic == TurnAction.CreateOrder),
        new("how do I mark an order as delivered", new[] { HowTo }, a => a.Topic == TurnAction.UpdateStatus),
        new("آرڈر کینسل کیسے ہوتا ہے", new[] { HowTo }, a => a.Topic == TurnAction.CancelOrder, Voice: true),
        // a contact number is a customer question, never support
        new("کیا آپ مجھے کانٹیکٹ نمبر بتا سکتے ہیں؟", new[] { Clarify, TurnAction.ShowCustomer }, Voice: true),
        new("Sara ka number kya hai", new[] { TurnAction.ShowCustomer }, a => Named(a, "Sara")),
        new("number bata do Sara Khan ka zara", new[] { TurnAction.ShowCustomer }, a => Named(a, "Sara")),
        new("Ali Raza kahan rehta hai", new[] { TurnAction.ShowCustomer }, a => Named(a, "Ali")),
        new("سارہ خان کا فون نمبر بتائیں", new[] { TurnAction.ShowCustomer }, a => Named(a, "Sara", "سارہ"), Voice: true),
        new("mere saare customers dikhao", new[] { TurnAction.ShowCustomer }, a => a.CustomerName is null),
        new("mujhe kisi banday se baat karni hai, masla hai", new[] { TurnAction.ContactSupport }),
        new("سپورٹ والوں سے بات کروا دیں", new[] { TurnAction.ContactSupport }, Voice: true),
        // catalog
        new("ek naya product daalna hai shawl 2500 ka", new[] { TurnAction.AddProduct }, a => Product(a, 2500, "shawl")),
        new("2500 ki hai shawl, catalog mein daal do", new[] { TurnAction.AddProduct }, a => Product(a, 2500, "shawl")),
        new("شال پچیس سو روپے کی ہے اسے لسٹ میں شامل کر دو", new[] { TurnAction.AddProduct }, a => Product(a, 2500), Voice: true),
        new("add a new item bedsheet for 1200", new[] { TurnAction.AddProduct }, a => Product(a, 1200, "bedsheet")),
        new("میرے پاس ایک نئی چیز آئی ہے چادر بارہ سو کی ہے", new[] { TurnAction.AddProduct }, a => Product(a, 1200), Voice: true),
        new("kurti ka rate ab 2000 hai", new[] { TurnAction.EditProduct }, a => Product(a, 2000, "kurti")),
        new("2000 kar do kurti ki qeemat", new[] { TurnAction.EditProduct }, a => Product(a, 2000, "kurti")),
        new("دوپٹے کی قیمت سات سو کر دیں", new[] { TurnAction.EditProduct }, a => Product(a, 700), Voice: true),
        new("meri cheezein dikhao jo main bechta hun", new[] { TurnAction.ShowCatalog }),
        new("kya kya products hain mere paas", new[] { TurnAction.ShowCatalog }),
        new("میری پروڈکٹس کی لسٹ دکھائیں", new[] { TurnAction.ShowCatalog }, Voice: true),
        // customers
        new("naya gahak hai Bilal Ahmed, Gulberg Lahore, 03001234567", new[] { TurnAction.AddCustomer }, a => Has(a.Customer?.Name, "Bilal") && Has(a.Customer?.Phone, "03001234567")),
        new("03001234567 Bilal Ahmed naam ka customer save kar lo", new[] { TurnAction.AddCustomer }, a => Has(a.Customer?.Name, "Bilal") && Has(a.Customer?.Phone, "03001234567")),
        new("میرا ایک نیا کسٹمر ہے جس کا نام بلال احمد ہے اور وہ گلبرگ میں رہتا ہے", new[] { TurnAction.AddCustomer }, a => a.Customer is not null && a.Customer.Phone is null, Voice: true),
        new("save a customer named Hina Tariq from Faisalabad", new[] { TurnAction.AddCustomer }, a => Has(a.Customer?.Name, "Hina")),
        new("Sara ka address badal do, ab DHA Phase 5 hai", new[] { TurnAction.EditCustomer }, a => Named(a, "Sara") && a.Field == "address" && Has(a.Value, "DHA")),
        new("Ali Raza ka naya number 03339998877 hai", new[] { TurnAction.EditCustomer }, a => Named(a, "Ali") && a.Field == "phone" && Has(a.Value, "03339998877")),
        new("03339998877 yeh Ali ka phone likh lo", new[] { TurnAction.EditCustomer }, a => Named(a, "Ali") && a.Field == "phone" && Has(a.Value, "03339998877")),
        // orders
        new("Sara ko do kurti bhejni hain", new[] { Order }, a => Buyer(a, "Sara") && Item(a, "kurti", 2)),
        new("do kurti aur ek dupatta, Sara Khan ke liye likh lo", new[] { Order }, a => Buyer(a, "Sara") && Item(a, "kurti", 2) && Item(a, "dupatta", 1)),
        new("kurti 2 Sara", new[] { Order }, a => Buyer(a, "Sara") && Item(a, "kurti", 2)),
        new("علی رضا کا آرڈر لکھیں تین لان سوٹ", new[] { Order }, a => Buyer(a, "Ali", "علی") && a.Orders[0].Items.Count == 1 && a.Orders[0].Items[0].Quantity == 3, Voice: true),
        new("likh lo ke Ali Raza ne mangwaye hain lawn suit, teen", new[] { Order }, a => Buyer(a, "Ali") && Item(a, "lawn", 3)),
        new("order for Sara Khan: 1 lawn suit and 2 dupatta, delivery 200", new[] { Order }, a => Buyer(a, "Sara") && Item(a, "lawn", 1) && Item(a, "dupatta", 2) && a.Orders[0].DeliveryCharge == 200),
        new("Hamza Sheikh 03451112223 ko ek kurti bhejni hai Johar Town Lahore", new[] { Order }, a => Buyer(a, "Hamza") && Item(a, "kurti", 1) && Has(a.Orders[0].Phone, "03451112223")),
        new("سارہ کو پانچ دوپٹے چاہیے", new[] { Order }, a => Buyer(a, "Sara", "سارہ") && a.Orders[0].Items.Count == 1 && a.Orders[0].Items[0].Quantity == 5, Voice: true),
        // status
        new("order 2 nikal gaya hai courier se", new[] { Status }, a => a.OrderId == 2 && a.Status == "shipped"),
        new("shipped kar do 2 number wala", new[] { Status }, a => a.OrderId == 2 && a.Status == "shipped"),
        new("تین نمبر آرڈر پہنچ گیا ہے گاہک کو", new[] { Status }, a => a.OrderId == 3 && a.Status == "delivered", Voice: true),
        new("wapas aa gaya hai parcel teen number ka", new[] { Status }, a => a.OrderId == 3 && a.Status == "returned"),
        new("Ali ka order bhej diya hai", new[] { Status }, a => (a.OrderId == 2 || Named(a, "Ali")) && a.Status == "shipped"),
        new("Sara Khan ko parcel mil gaya", new[] { Status }, a => (a.OrderId is not null || Named(a, "Sara")) && a.Status == "delivered"),
        new("order 1 cancel kar do", new[] { TurnAction.CancelOrder }, a => a.OrderId == 1),
        new("دو نمبر آرڈر کینسل کر دیں", new[] { TurnAction.CancelOrder }, a => a.OrderId == 2, Voice: true),
        // lists and summary
        new("aaj kitne order aaye", new[] { TurnAction.ShowOrders, TurnAction.ShowSummary }),
        new("orders dikhao aaj wale", new[] { TurnAction.ShowOrders }),
        new("konse order abhi tak nahi bheje", new[] { TurnAction.ShowOrders }, a => a.PendingOnly),
        new("order number 1 ki detail", new[] { TurnAction.ShowOrder }, a => a.OrderId == 1),
        new("Ali ke order dikhao", new[] { TurnAction.ShowOrders, TurnAction.ShowCustomer, TurnAction.ShowOrder }, a => Named(a, "Ali") || a.OrderId == 2),
        new("kya aap mujhe contact number bata sakte hain", new[] { Clarify, TurnAction.ShowCustomer }),
        new("customer ka number chahiye", new[] { Clarify, TurnAction.ShowCustomer }),
        new("aaj kitni sale hui", new[] { TurnAction.ShowSummary }, a => a.Period is null or "today"),
        new("kal ka hisaab batao", new[] { TurnAction.ShowSummary }, a => a.Period == "yesterday"),
        new("اس ہفتے کتنی سیل ہوئی", new[] { TurnAction.ShowSummary }, a => a.Period == "this_week", Voice: true),
        // a product that comes in a carton: the model only reports what was said, it never divides
        new("sarson ka tel hai, carton 1000 ka aata hai, ek carton mein 10 piece, 3 carton hain, piece 140 ka aur carton 1200 ka bechta hun", new[] { TurnAction.AddProduct },
            a => Pack(a, qty: 10, cost: 1000, price: 1200, stockPacks: 3) && a.Products[0].Price == 140),
        new("mere paas sarson ke tel ki product hai jo carton mein aati hai, fi carton 1000 rupay ka hai aur us mein 10 pieces aate hain, mere paas 3 carton hain, fi piece 130 se 140 mein bechna hai aur carton 1150 se 1200 mein",
            new[] { TurnAction.AddProduct }, a => Pack(a, qty: 10, cost: 1000, price: 1200, stockPacks: 3) && a.Products[0].Price == 140),
        new("صابن کی پیٹی آٹھ سو کی آتی ہے اس میں بارہ پیس ہوتے ہیں ایک پیس اسی روپے کا بیچتا ہوں", new[] { TurnAction.AddProduct },
            a => Pack(a, qty: 12, cost: 800, price: null, stockPacks: null) && a.Products[0].Price == 80, Voice: true),
        new("Ali Raza ko 2 carton tel bhej do", new[] { Order }, a => Buyer(a, "Ali") && Packs(a, 2)),
        new("Sara ko ek carton tel 1150 ka de do", new[] { Order }, a => Buyer(a, "Sara") && Packs(a, 1) && a.Orders[0].Items[0].StatedUnitPrice == 1150),
        new("علی رضا کو تیل کے تین کارٹن بھیج دیں", new[] { Order }, a => Buyer(a, "Ali", "علی") && Packs(a, 3), Voice: true),
        new("Ali ko 3 tel bhej do", new[] { Order }, a => Buyer(a, "Ali") && a.Orders[0].Items.Count == 1 && a.Orders[0].Items[0].Quantity == 3 && !a.Orders[0].Items[0].InPacks),
        new("tel ke 2 carton aur aa gaye hain", new[] { TurnAction.AddStock }, a => a.Products.Count == 1 && a.Products[0].StockPacks == 2),
        new("kurti 5 aur aa gayi hain", new[] { TurnAction.AddStock }, a => a.Products.Count == 1 && a.Products[0].Stock == 5 && a.Products[0].StockPacks is null),
        // not the shop, or not built yet
        new("assalam o alaikum kya haal hai", new[] { TurnAction.HelpOrChat }),
        new("mausam kaisa hai aaj", new[] { TurnAction.HelpOrChat, TurnAction.OutOfScope }),
        new("petrol ka 500 kharcha likh lo", new[] { TurnAction.OutOfScope }),
        new("ek kurti", new[] { Clarify, Order }),
    };

    private static readonly TurnContext Shop = new()
    {
        BusinessName = "Ayesha Collection",
        ShopType = "Kapray / Lawn / Boutique",
        Catalog = new[]
        {
            new AiCatalogItem { Name = "Kurti", Price = 1800 }, new AiCatalogItem { Name = "Lawn Suit", Price = 3500 }, new AiCatalogItem { Name = "Dupatta", Price = 600 },
            new AiCatalogItem { Name = "Sarson Tel", Price = 140 },
        },
        CatalogSize = 4,
        KnownCustomers = new[] { "Sara Khan (03004445566, Model Town Lahore)", "Ali Raza (03215557788, Saddar Karachi)" },
        RecentOrders = new[] { "#3 Sara Khan Shipped", "#2 Ali Raza Pending", "#1 Sara Khan Pending" },
    };

    [Fact]
    public async Task Free_form_sentences_are_understood()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;
        var options = new OpenAiOptions { ApiKey = key };
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL_MODEL") is { Length: > 0 } model) options.Model = model;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var interpreter = new OpenAiTurnInterpreter(http, Options.Create(options), NullLogger<OpenAiTurnInterpreter>.Instance);

        var gate = new SemaphoreSlim(6);
        var results = await Task.WhenAll(Cases.Select(async c =>
        {
            await gate.WaitAsync();
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var context = new TurnContext
                {
                    BusinessName = Shop.BusinessName, ShopType = Shop.ShopType, Catalog = Shop.Catalog, CatalogSize = Shop.CatalogSize,
                    KnownCustomers = Shop.KnownCustomers, RecentOrders = Shop.RecentOrders, FromVoice = c.Voice,
                };
                var action = await interpreter.UnderstandAsync(context, c.Message);
                var right = action is not null && c.Actions.Contains(action.Action) && (c.Check is null || c.Check(action));
                return (Case: c, Action: action, Right: right, Ms: clock.ElapsedMilliseconds);
            }
            finally { gate.Release(); }
        }));

        foreach (var r in results.Where(r => !r.Right))
            _output.WriteLine($"WRONG  \"{r.Case.Message}\"\n       wanted {string.Join(" / ", r.Case.Actions)}, got {Describe(r.Action)}");
        var right = results.Count(r => r.Right);
        var times = results.Select(r => r.Ms).OrderBy(ms => ms).ToList();
        _output.WriteLine($"\n{options.Model}: {right}/{results.Length} right ({100.0 * right / results.Length:0}%); latency median {times[times.Count / 2]} ms, max {times[^1]} ms");
        Assert.True(right >= results.Length * PassRate, $"{right}/{results.Length} right, below {PassRate:P0}");
    }

    private static string Describe(TurnAction? a) => a is null ? "nothing" :
        $"{a.Action} topic={a.Topic} order={a.OrderId} status={a.Status} period={a.Period} customer={a.CustomerName} field={a.Field} value={a.Value} " +
        $"new={a.Customer?.Name}/{a.Customer?.Phone} products=[{string.Join(", ", a.Products.Select(p => $"{p.Name} price={p.Price} cost={p.Cost} stock={p.Stock} pack={p.PackName}/{p.PackQty} packCost={p.PackCost} packPrice={p.PackPrice} stockPacks={p.StockPacks}"))}] " +
        $"orders=[{string.Join("; ", a.Orders.Select(o => $"{o.CustomerName}: {string.Join(", ", o.Items.Select(i => $"{i.ProductName} x{i.Quantity}{(i.InPacks ? " packs" : "")} @{i.StatedUnitPrice}"))}"))}] q={a.Question}";

    // Offline: the expected answers themselves are sane (every case names real actions), so a typo here cannot hide a failure.
    [Fact]
    public void Every_case_names_known_actions()
    {
        Assert.All(Cases, c => Assert.All(c.Actions, a => Assert.Contains(a, TurnAction.All)));
        Assert.True(Cases.Length >= 50);
    }
}
