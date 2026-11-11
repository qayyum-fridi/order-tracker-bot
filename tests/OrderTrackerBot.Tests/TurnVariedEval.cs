using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// About 300 varied sentences written by hand, not from templates: different word order, slang, half-finished sentences, typos, mixed scripts,
/// several requests in one message, and edits. Labels are the sensible reading. Still written by the author, not by real sellers.
/// Skipped unless OTB_TURN_EVAL=1 and OPENAI_API_KEY are set:
/// <c>OTB_TURN_EVAL=1 OPENAI_API_KEY=... dotnet test --filter TurnVariedEval --logger "console;verbosity=detailed"</c>
/// </summary>
public class TurnVariedEval
{
    private const double PassRate = 0.90;
    private readonly ITestOutputHelper _output;
    public TurnVariedEval(ITestOutputHelper output) => _output = output;

    private const string Order = TurnAction.CreateOrder, Status = TurnAction.UpdateStatus, Cancel = TurnAction.CancelOrder,
        ShowOrders = TurnAction.ShowOrders, ShowOrder = TurnAction.ShowOrder, Summary = TurnAction.ShowSummary, Catalog = TurnAction.ShowCatalog,
        AddProduct = TurnAction.AddProduct, EditProduct = TurnAction.EditProduct, Customer = TurnAction.ShowCustomer, AddCustomer = TurnAction.AddCustomer,
        EditOrder = TurnAction.EditOrder, Clarify = TurnAction.Clarify, Chat = TurnAction.HelpOrChat, Out = TurnAction.OutOfScope, HowTo = TurnAction.HowTo;

    internal sealed record Case(string Message, string[] Actions, bool Voice = false, int MinActions = 1);

    private static Case T(string m, params string[] a) => new(m, a);
    private static Case V(string m, params string[] a) => new(m, a, Voice: true);
    private static Case M(string m, int min, params string[] a) => new(m, a, MinActions: min);

    internal static readonly Case[] Cases =
    {
        // ---- orders (varied wording, no template) ----
        T("ye lo bhai, Zohaib ke liye 3 shirt pack kar do, kal bhejni hain", Order),
        V("bhai mujhe Hamza ke liye do lawn suit chahiye bhej dena", Order),
        T("sister ne 4 kurti mangwai hain, address wahi purana wala", Order),
        V("Mehwish appa ke liye ek shawl aur dupatta bhej dein gulshan wali", Order),
        T("order chahiye: 2 bags, 1 chappal, Rehan ko", Order),
        V("Rehan ko 2 bag aur ek chappal likh do", Order),
        T("Dua 5 dupatta cod mein bhej do please", Order),
        V("Dua ko paanch dupatta, cash on delivery", Order),
        T("can you add an order for Kashif: two shirts and a kurta", Order),
        V("ek order likh do Kashif ka, do shirt aur ek kurta", Order),
        T("send 6 shawl to Nimra, cod", Order),
        V("Nimra ko chhe shawl send kar do", Order),
        T("Saira baji ka order, 1 lawn suit 1 dupatta, delivery 200 lagana", Order),
        V("saira baji ko lawn suit aur dupatta bhej dein, delivery do sau", Order),
        T("new customer Asim, phone 03001112233 aur 2 shirt", Order),
        V("Asim ke liye do shirt, number zero teen aik ek wan wan wan teen", Order),
        T("ek kurti Sadia ko chahiye jaldi", Order),
        V("Sadia ko jaldi se ek kurti chahiye", Order),
        T("order for Ayesha 3 shawl advance 1000", Order),
        V("Ayesha ke teen shawl likh do, ek hazar advance mil gaya", Order),
        T("sab kuch Farah ke naam: 2 shirt 2 pant", Order),
        V("Farah ke naam do shirt aur do pant", Order),
        T("Tariq ka 1 bag bhej dena, Lahore", Order),
        V("Tariq ko ek bag bhejna hai Lahore mein", Order),
        T("2 pack bangles Anum ko", Order),
        V("Anum ko do pack churiyan", Order),
        T("Hassan 1 shoes 42 size", Order),
        V("Hassan ko ek joota, bayalis size", Order),
        T("kal ki delivery ke liye Bushra ko 2 suit", Order),
        V("bushra ko kal ke liye do suit chahiyen", Order),
        T("shirt 3, Mariam, cod, Model Town", Order),
        V("Mariam ko teen shirt, cod, model town", Order),
        T("Rabia ke liye kurti ka set 2", Order),
        V("Rabia ka kurti set do wala", Order),
        T("order 1 shawl Naveed, 1 shawl Zain", Order),
        V("Naveed ko ek shawl aur Zain ko ek shawl", Order),
        T("Yasir ka order: 4 lawn", Order),
        V("Yasir ke chaar lawn likh do", Order),
        T("wholesale 10 shirt Kamran shop ke liye", Order),
        V("Kamran ki dukan ke liye das shirt", Order),
        T("Zara ke liye 2 bags aur 1 purse, delivery free", Order),
        V("Zara ko do bag aur ek purse, delivery free rakhna", Order),
        T("Sohail 1 kurti 1 dupatta payment cash", Order),
        V("Sohail ko ek kurti ek dupatta, cash", Order),
        T("need an order: Noor, 2 chappal", Order),
        V("Noor ke liye do chappal ka order", Order),
        T("Shabnam ko 3 suit wali deal de dena", Order),
        V("Shabnam ko teen suit wali deal bhej dena", Order),
        T("bhej do Adeel ko 5 dupatta", Order),
        V("Adeel ko paanch dupatta bhej do", Order),
        T("Gul Bano ka order 2 shalwar kameez", Order),
        V("gul bano ke do shalwar kameez", Order),
        T("Mohsin ke 1 shirt aur 1 pant", Order),
        V("Mohsin ko shirt aur pant ek ek", Order),
        T("order: Kiran (0333 4445556) 2 lawn suit", Order),
        V("Kiran ka order, teen tin teen teen, lawn suit do", Order),
        T("Jawad ko 1 bag 1 chappal delivery 150", Order),
        V("jawad ko ek bag aur ek chappal, delivery aik sau pachaas", Order),
        T("Ghazala 2 kurti aur 2 shawl", Order),
        V("Ghazala ko do kurti aur do shawl", Order),
        T("order chaiye Waseem, 1 pant", Order),
        V("Waseem ka ek pant wala order", Order),
        T("Sadaf ka phone 0345 1122334 aur 2 dupatta", Order, AddCustomer),
        V("Sadaf ka number zero teen chaar, do dupatta", Order),
        T("Huma 4 shirt wholesale rate pe", Order),
        V("Huma ko chaar shirt wholesale rate par", Order),
        T("Imtiaz ke liye 2 pack socks", Order),
        V("Imtiaz ko do pack socks", Order, Clarify),
        T("1 lawn suit for Mahnoor", Order),
        V("Mahnoor ko ek lawn suit", Order, Clarify),
        T("3 bags ko Alia", Order, Clarify),
        V("Alia ko teen bag", Order, Clarify),
        T("Sana ka 2 kurti cod delivery Karachi", Order),
        V("Sana ko do kurti cod karachi", Order),
        T("Qasim 2 shirt bhej do jaldi", Order),
        V("Qasim ko do shirt jaldi bhej do", Order),
        T("Fareed 1 kurta 2 pant", Order),
        V("Fareed ko ek kurta do pant", Order, Clarify),
        T("Shazia ke 5 shawl", Order),
        V("Shazia ko paanch shawl", Order),
        T("order Maryam 1 kurti", Order),
        V("Maryam ka ek kurti wala", Order),
        T("bhej do Rukhsana 2 dupatta and 1 bag", Order),
        V("Rukhsana ko do dupatta aur ek bag", Order),
        T("Arshad 3 chappal, Gujranwala", Order),
        V("Arshad ko teen chappal gujranwala", Order),
        T("Irum ke liye 2 suit aur delivery 250", Order),
        V("Irum ko do suit aur delivery dhai sau", Order),
        T("order Fawad 1 lawn 1 shawl 1 bag", Order),
        V("Fawad ka ek lawn, ek shawl, ek bag", Order),
        T("Naima 6 bangles", Order),
        V("Naima ko chhe churiyan", Order),
        T("Sohrab 2 jora joote", Order),
        V("Sohrab ko do jora joote", Order),
        T("order Shagufta 3 kurti 3 dupatta", Order),
        V("Shagufta ko teen kurti aur teen dupatta", Order),

        // ---- status and payment ----
        T("Mehwish ka order mil gaya", Status),
        V("Mehwish appa ko order mil gaya", Status),
        T("order 7 ka status deliver", Status),
        V("saat number order deliver ho gaya", Status),
        T("Ali bhai ka parcel wapas aa gaya hai", Status),
        V("Ali bhai ka parcel wapis aa gaya", Status),
        T("order 2 shipped kar dena", Status),
        V("do number ko shipped kar dena", Status),
        T("Rehan ka order cancel karna hai", Cancel),
        V("Rehan ka order cancel kar do", Cancel),
        T("cancel order 9", Cancel),
        V("nau number cancel karo", Cancel),
        T("order 4 ko pending kar do", Status),
        V("chaar number wapas pending kar do", Status),
        T("Dua ne 2000 bhej diye bank se", Out, Clarify),
        V("Dua ne do hazaar bhej diye", Out, Clarify),
        T("order 5 ka balance kitna hai", Out, Clarify, ShowOrder),
        V("paanch number ka baaki kitna hai", Out, Clarify, ShowOrder),
        T("kitne parcel pending hain", ShowOrders),
        V("pending parcel kitne hain", ShowOrders),
        T("Zara wali order ka status kya hai", ShowOrder, ShowOrders),
        V("Zara ka order kis halat mein hai", ShowOrder),
        T("show me order 6", ShowOrder),
        V("chhe number dikhao", ShowOrder),
        T("aaj kitne orders aaye", ShowOrders),
        V("aaj ke kitne order aaye", ShowOrders),
        T("Imran ka order shipped ho gaya kya", ShowOrder, Status),
        V("Imran ka order gaya kya", ShowOrder, Status, Clarify),
        T("order 8 delivered mark karo", Status),
        V("aath number delivered mark kar do", Status),
        T("Hira ne payment kar di", Out, Clarify),
        V("Hira ne paisay de diye", Out, Clarify),
        T("order 3 full paid", Out, Clarify, Status),
        V("teen number poora paid ho gaya", Out, Clarify, Status),
        T("mark order 10 returned", Status),
        V("das number wapis aa gaya", Status),
        T("delivered: order 11", Status),
        V("gyarah number deliver", Status),
        T("Rabia ka order cancel kar dena please", Cancel),
        V("Rabia ka order cancel kar dein", Cancel),
        T("pending orders dikhao sab", ShowOrders),
        V("sab pending orders dikhao", ShowOrders),
        T("Sohail ka parcel shipped kar diya kya", Status, ShowOrder),
        V("Sohail ka parcel bhej diya kya", Status, ShowOrder),
        T("cancel 12", Cancel),
        V("barah cancel", Cancel),
        T("order 14 deliver nahi hua abhi tak", ShowOrder, Status, Clarify),
        V("chaudah number abhi tak deliver nahi hua", ShowOrder, Status, Clarify),
        T("returned order 15", Status),
        V("pandrah wapas wala", Status),

        // ---- questions ----
        T("aaj ki sale kaisi rahi", Summary),
        V("aaj kitna maal bika", Summary),
        T("kal kitna kamaya", Summary),
        V("kal ka kitna bana", Summary),
        T("Zohaib ka number do", Customer),
        V("Zohaib ka phone number batao", Customer),
        T("kahan rehta hai Hamza", Customer),
        V("Hamza kahan ka hai", Customer),
        T("saare customers ki list", Customer),
        V("customer list dikhao", Customer),
        T("Mehwish ka address kya tha", Customer),
        V("Mehwish appa ka pata batao", Customer),
        T("customer kaise add karun", HowTo),
        V("naya customer kaise dalun", HowTo),
        T("product kaise delete karte hain", HowTo),
        V("product ko kaise hatayein", HowTo),
        T("order kaise edit karte hain", HowTo),
        V("order mein change kaise karein", HowTo),
        T("kaise pata chalega ke kitna stock bacha", HowTo, Clarify),
        V("stock kitna bacha hai kaise dekhun", HowTo, Clarify),
        T("is mahine ka hisaab", Summary),
        V("is mahine ka total kya hai", Summary),
        T("kitne customers hain", Customer),
        V("mere kitne customer hain", Customer),
        T("what is my sale today", Summary),
        V("how much did i sell today", Summary),
        T("show orders for this week", ShowOrders),
        V("this week ke orders dikhao", ShowOrders),
        T("which orders are pending", ShowOrders),
        V("kaun se order pending hain", ShowOrders),
        T("Sara Khan ka number", Customer),
        V("Sara Khan ka phone", Customer),
        T("kya koi order cancel hua aaj", ShowOrders, Summary),
        V("aaj koi order cancel hua kya", ShowOrders, Summary),
        T("catalog mein kitne products hain", Catalog),
        V("kitne product hain meri list mein", Catalog),
        T("hisaab kitab dikhao aaj ka", Summary),
        V("aaj ka hisaab dikha do", Summary),
        T("Ali ka address bata do", Customer),
        V("Ali ka pata do", Customer),
        T("kitna profit hua is hafte", Out, Summary),
        V("is hafte kitna munafa hua", Out, Summary),
        T("Ayesha ke saare order", ShowOrders, Customer),
        V("Ayesha ke sab order dikhao", ShowOrders, Customer),
        T("help", Chat, HowTo),
        V("madad chahiye", Chat, HowTo),
        T("how do i use this bot", HowTo, Chat),
        V("bot kaise chalaun", HowTo, Chat),

        // ---- catalog ----
        T("apne products dikhao", Catalog),
        V("saray products ki list", Catalog),
        T("naya product lawn 2 piece 3500", AddProduct),
        V("ek naya product daal do, lawn do piece, teen hazar paanch sau", AddProduct),
        T("Kurti Red - 1200 add kar do", AddProduct),
        V("Kurti Red bara sau par daal do", AddProduct),
        T("Shirt Blue ka rate 1900 kar do", EditProduct),
        V("Shirt Blue ki keemat unnis sau kar do", EditProduct),
        T("Dupatta ka rate 800", EditProduct),
        V("Dupatta ko aath sau kar do", EditProduct),
        T("product list", Catalog),
        V("list dikhao products ki", Catalog),
        T("new item shawl 2800", AddProduct),
        V("shawl naya hai, 2800 ka", AddProduct),
        T("catalog bhejo", Catalog),
        V("catalog dikha dein", Catalog),
        T("Bag ka price 2200 kar do", EditProduct),
        V("bag ka daam do hazar do sau", EditProduct),
        T("naya product Chappal 1100", AddProduct),
        V("chappal naya hai, gyarah sau", AddProduct),
        T("show me the catalog", Catalog),
        V("catalog show karo", Catalog),
        T("shirt ka rate kya hai", Catalog, Clarify),
        V("shirt ka rate batao", Catalog, Clarify),

        // ---- chat or unclear ----
        T("assalamualaikum", Chat),
        V("walaikum assalam bhai", Chat),
        T("thanks a lot", Chat),
        V("bohat shukriya", Chat),
        T("ok done", Chat, Clarify),
        V("theek hai bhai", Chat, Clarify),
        T("...", Clarify, Chat),
        V("ahan", Clarify, Chat),
        T("good night", Chat),
        V("allah hafiz", Chat),
        T("are you there", Chat),
        V("hello kya haal", Chat),
        T("test", Chat, Clarify),
        V("check", Chat, Clarify),
        T("nice bot", Chat),
        V("bahut acha bot hai", Chat),
        T("lol", Chat, Clarify),
        V("haha", Chat, Clarify),
        T("who made you", Chat),
        V("tum kon ho", Chat),

        // ---- several requests in one message ----
        M("Zohaib ka order 2 shirt aur order 3 shipped kar do", 2, Order, Status),
        M("show pending orders aur aaj ki sale batao", 2, ShowOrders, Summary),
        M("Sana ke 2 kurti likh do aur Kiran ka number do", 2, Order, Customer),
        M("ek naya product shawl 2000 aur catalog dikhao", 2, AddProduct, Catalog),
        M("order 4 cancel karo aur Hamza ko 1 bag bhej do", 2, Cancel, Order),
        M("Rehan ka order mil gaya aur aaj ka hisaab", 2, Status, Summary),
        M("Mariam ko 2 shirt bhej do, order 9 deliver karo", 2, Order, Status),
        M("kal ki sale aur pending orders", 2, Summary, ShowOrders),
        M("Ayesha ke 3 suit likho aur ek naya customer Noor, 0300 1234567", 2, Order, AddCustomer),
        M("Hassan ko 1 joota likho aur order 5 cancel", 2, Order, Cancel),
        M("shirt ka rate 2100 karo aur catalog dikhao", 2, EditProduct, Catalog),
        M("pending batao aur order 2 advance 500 lo", 2, ShowOrders, Out),
        M("naya customer Hina 03211234567 aur Hina ka order 1 dupatta", 2, AddCustomer, Order),
        M("order 6 delivered aur Zara ka number", 2, Status, Customer),
        M("sab customers dikhao, aur aaj ki sale", 2, Customer, Summary),

        // ---- edits to an order's items ----
        T("order 3 mein shirt 2 se 4 kar do", EditOrder),
        V("teen number mein shirt do se chaar kar do", EditOrder),
        T("order 5 se kurti hata do", EditOrder),
        V("paanch number se kurti nikal do", EditOrder),
        T("order 2 mein ek dupatta aur add karo", EditOrder),
        V("do number mein ek dupatta aur daal do", EditOrder),
        T("Sana ke order mein shawl ka rate 2000 karo", EditOrder),
        V("Sana ke order mein shawl ki qeemat do hazar kar do", EditOrder),
        T("order 8 ki quantity 1 kar do", EditOrder),
        V("aath number ki quantity aik kar do", EditOrder),
        T("order 4 mein bag remove karo", EditOrder),
        V("chaar number se bag hata do", EditOrder),
        T("order 1 kurti 3 karo", EditOrder),
        V("aik number kurti teen kar do", EditOrder),
        T("order 6 ka kurta delete", EditOrder),
        V("chhe number ka kurta hata dein", EditOrder),
        T("Hamza ke order mein 1 chappal aur daal do", EditOrder),
        V("Hamza ke order mein ek chappal aur", EditOrder, Clarify),
        T("order 9 mein lawn ka price 3000", EditOrder),
        V("nau number mein lawn teen hazar", EditOrder),
        T("order 7 kurti 2 ki jagah 5", EditOrder),
        V("saat number kurti do ki jagah paanch", EditOrder),
        T("order 10 mein shirt ki qeemat 1500", EditOrder),
        V("das number shirt daam pandrah sau", EditOrder),
        T("order 11 se shawl nikal do aur 2 bag daal do", EditOrder, Clarify),
        V("gyarah number se shawl hata do, do bag daal do", EditOrder, Clarify),
        T("order 12 mein 2 kurti 3 kar do", EditOrder),
        V("barah number mein do kurti teen kar do", EditOrder),
    };

    private static readonly string[] ShopProducts = { "Shirt - 1800", "Kurti - 1200", "Shawl - 2500" };
    private static readonly string[] ShopCustomers = { "Sara Khan (03004445566, Model Town Lahore)", "Ali Raza (03215557788, Saddar Karachi)", "Bilal Ahmed (03001234567, Gulberg Lahore)" };
    private static readonly string[] ShopOrders = { "#3 Sara Khan Shipped", "#2 Ali Raza Pending", "#1 Sara Khan Pending" };

    [Fact]
    public async Task Varied_sentences_are_understood_by_the_real_model()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;
        var options = new OpenAiOptions { ApiKey = key };
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL_MODEL") is { Length: > 0 } model) options.Model = model;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var interpreter = new OpenAiTurnInterpreter(http, Options.Create(options), NullLogger<OpenAiTurnInterpreter>.Instance);

        var catalog = ShopProducts.Select(p => new AiCatalogItem { Name = p.Split(" - ")[0], Price = decimal.Parse(p.Split(" - ")[1]) }).ToList();
        var gate = new SemaphoreSlim(6);
        var results = await Task.WhenAll(Cases.Select(async c =>
        {
            await gate.WaitAsync();
            try
            {
                var context = new TurnContext
                {
                    BusinessName = "Ayesha Collection", ShopType = "Clothing", CatalogSize = catalog.Count, Catalog = catalog,
                    KnownCustomers = ShopCustomers, RecentOrders = ShopOrders, FromVoice = c.Voice,
                };
                var action = await interpreter.UnderstandAsync(context, c.Message);
                var count = action is null ? 0 : 1 + action.Also.Count;
                var right = action is not null && c.Actions.Contains(action.Action) && count >= c.MinActions;
                return (Case: c, Action: action, Right: right, Count: count);
            }
            finally { gate.Release(); }
        }));

        foreach (var r in results.Where(r => !r.Right))
            _output.WriteLine($"WRONG  [{(r.Case.Voice ? "voice" : "typed")}] \"{r.Case.Message}\"\n       wanted {string.Join(" / ", r.Case.Actions)} (at least {r.Case.MinActions} actions), got {r.Action?.Action ?? "nothing"} ({r.Count} actions)");
        var right = results.Count(r => r.Right);
        _output.WriteLine($"\n{options.Model} varied: {right}/{results.Length} right ({100.0 * right / results.Length:0}%)");
        Assert.True(right >= results.Length * PassRate, $"{right}/{results.Length} right, below {PassRate:P0}");
    }

    // Offline: about 300 sentences, every expected answer is a real action, and there are several multi-request cases.
    [Fact]
    public void Varied_set_names_real_actions_and_has_multi_request_cases()
    {
        Assert.InRange(Cases.Length, 250, 350);
        Assert.All(Cases, c => Assert.All(c.Actions, a => Assert.Contains(a, TurnAction.All)));
        Assert.True(Cases.Count(c => c.MinActions >= 2) >= 10);
    }
}
