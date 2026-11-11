using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// The real-model check for the traffic mix a shop actually sends: about 40% orders, 20% status and payment, 20% questions, 10% catalog,
/// 10% chat or unclear; half typed, half voice transcripts (<see cref="TurnContext.FromVoice"/>); Roman Urdu, Urdu script and English.
/// Skipped unless OTB_TURN_EVAL=1 and OPENAI_API_KEY are set:
/// <c>OTB_TURN_EVAL=1 OPENAI_API_KEY=... dotnet test --filter TurnMixEval --logger "console;verbosity=detailed"</c>
/// </summary>
public class TurnMixEval
{
    private const double PassRate = 0.95;
    private readonly ITestOutputHelper _output;
    public TurnMixEval(ITestOutputHelper output) => _output = output;

    private const string Order = TurnAction.CreateOrder, Status = TurnAction.UpdateStatus, Cancel = TurnAction.CancelOrder,
        ShowOrders = TurnAction.ShowOrders, ShowOrder = TurnAction.ShowOrder, Summary = TurnAction.ShowSummary, Catalog = TurnAction.ShowCatalog,
        AddProduct = TurnAction.AddProduct, EditProduct = TurnAction.EditProduct, Customer = TurnAction.ShowCustomer, Clarify = TurnAction.Clarify,
        Chat = TurnAction.HelpOrChat, Out = TurnAction.OutOfScope, HowTo = TurnAction.HowTo;

    /// <summary>One row: the seller's words, the answers the model may give, and whether the text is a voice transcript.</summary>
    internal sealed record Row(string Message, string[] Actions, bool Voice);

    private static Row T(string m, string a) => new(m, new[] { a }, false);
    private static Row V(string m, string a) => new(m, new[] { a }, true);

    internal static readonly Row[] Mix =
    {
        // ---- orders (40%) ----
        T("Sara ko 2 shirt bhej do, 03004445566, Model Town Lahore", Order),
        V("Ali ke liye teen kurti aur ek shawl likh do", Order),
        T("ali 2 shirt 1 kurti", Order),
        V("گل کو دو شرٹ اور ایک کرتی بھیج دیں", Order),
        T("Bilal Ahmed ka order: 1 shawl, 0300 1234567, Gulberg Lahore", Order),
        V("سارہ کے لیے ایک شال، کیش آن ڈیلیوری", Order),
        T("new order sara khan 3 shirt delivery 200", Order),
        V("Sara ko teen shirt aur delivery 150 likh do", Order),
        T("Ali Raza ko 1 kurti bhej do address Saddar Karachi", Order),
        V("ali raza ka order do kurti, saddar karachi", Order),
        T("bilal ko 2 shawl, delivery 300", Order),
        V("bilal ahmed ko dono shawl bhej dein", Order),
        T("Sara Khan 1 shirt 1 kurti cod", Order),
        V("سارہ کو ایک کرتی اور ایک شرٹ", Order),
        T("order likho: Ali, 2 shawl, 2500 wali", Order),
        V("Ali ka order, do shawl dhai hazar paanch sau wale", Order),
        T("Sara 5 shirt aur 2 kurti, advance 1000", Order),
        V("سارہ کے لیے پانچ شرٹ اور دو کرتی، ایڈوانس ہزار", Order),
        T("Bilal 1 kurti 1200 mein", Order),
        V("bilal ko ek kurti baara sau mein", Order),
        T("sara ko 3 shirt, bilal ko 1 shawl, dono ka order", Order),
        V("Ali 2 kurti aur Sara 1 shawl bhej dein", Order),
        T("naya client Muhammad Imran, 0321 5556677, shirt 2 chahiye", Order),
        V("Imran ko do shirt chahiye, number 03215556677", Order),
        T("Ali ko 2 shirt aur 1 kurti bhej do, delivery 250", Order),
        V("علی کو دو شرٹ اور ایک کرتی، ڈیلیوری ڈھائی سو", Order),
        T("order for Sara: 1 shirt", Order),
        V("Sara ke liye ek shirt likh do", Order),
        T("kal ke liye Ali ko 4 kurti order karo", Order),
        V("کل علی کو چار کرتی بھیجنی ہیں", Order),
        T("Sara Khan 2 shawl cod delivery free", Order),
        V("سارہ کو دو شال، ڈیلیوری فری", Order),
        T("bilal 2 shirt aur 2 kurti", Order),
        V("bilal ko do shirt aur do kurti", Order),
        T("add order Ali 1 shawl 2500", Order),
        V("Ali ka ek shawl dhai hazar paanch sau", Order),
        T("Sara ko 1 kurti, aur Bilal ko 1 shirt", Order),
        V("سارہ کو ایک کرتی اور بلال کو ایک شرٹ", Order),
        T("ali 3 shirt 1 shawl 1 kurti, phone 0321 5556677", Order),
        V("Ali ko teen shirt, ek shawl, ek kurti", Order),

        // ---- status / payment (20%) ----
        T("order 2 shipped kar do", Status),
        V("دو نمبر آرڈر بھیج دیا", Status),
        T("Sara ka order deliver ho gaya", Status),
        V("سارہ کا آرڈر پہنچ گیا", Status),
        T("Ali ka order wapas aa gaya", Status),
        V("علی کا آرڈر واپس آ گیا", Status),
        T("mark 3 delivered", Status),
        V("teen number delivered kar do", Status),
        T("order 1 cancel karo", Cancel),
        V("ایک نمبر آرڈر کینسل کر دیں", Cancel),
        T("Bilal ka order cancel kar dein", Cancel),
        V("بلال کا آرڈر منسوخ کر دو", Cancel),
        // Payments are not a bot action: asking or saying it is out of scope; a status change would be wrong.
        new("order 2 advance 500", new[] { Out, Clarify }, false),
        new("دو نمبر آرڈر پر پانچ سو ایڈوانس لے لیے", new[] { Out, Clarify }, true),
        new("Sara ne 1000 de diye, payment mark karo", new[] { Out, Clarify }, false),
        new("سارہ نے ہزار دے دیے ہیں", new[] { Out, Clarify }, true),
        T("pending orders dikhao", ShowOrders),
        V("کون کون سے آرڈر ابھی باقی ہیں؟", ShowOrders),
        // One order's status or the list of orders: either answer is reasonable.
        new("Ali ka order pending hai ya shipped", new[] { ShowOrder, ShowOrders }, false),
        new("علی کا آرڈر کس حال میں ہے", new[] { ShowOrder, ShowOrders }, true),
        T("order 3 kaisa hai", ShowOrder),
        V("تین نمبر آرڈر دکھاؤ", ShowOrder),

        // ---- questions (20%) ----
        T("aaj kitna bika", Summary),
        V("آج کتنی سیل ہوئی", Summary),
        T("kal ka hisaab batao", Summary),
        V("کل کا حساب بتائیں", Summary),
        T("Sara ka number kya hai", Customer),
        V("سارہ کا نمبر کیا ہے", Customer),
        T("Ali Raza kahan rehta hai", Customer),
        V("علی رضا کہاں رہتا ہے", Customer),
        T("mere customers dikhao", Customer),
        V("میرے سارے کسٹمر دکھاؤ", Customer),
        T("is hafte kitne orders aaye", ShowOrders),
        V("اس ہفتے کتنے آرڈر آئے", ShowOrders),
        T("order kaise delete karun", HowTo),
        V("آرڈر کیسے ڈیلیٹ کرتے ہیں", HowTo),
        T("product kaise add karun", HowTo),
        V("نیا پروڈکٹ کیسے ڈالوں", HowTo),
        T("customer ka address kaise change karun", HowTo),
        V("کسٹمر کا پتہ کیسے بدلوں", HowTo),
        T("kitne pending orders hain", ShowOrders),
        V("کتنے آرڈر پینڈنگ ہیں", ShowOrders),

        // ---- catalog (10%) ----
        T("catalog dikhao", Catalog),
        V("کیٹلاگ دکھاؤ", Catalog),
        T("mere products ki list", Catalog),
        V("میری پروڈکٹس کی لسٹ دکھائیں", Catalog),
        T("naya product shawl 2500 ka", AddProduct),
        V("شال پچیس سو کی ہے، لسٹ میں ڈال دو", AddProduct),
        T("shirt ka rate 2000 kar do", EditProduct),
        V("شرٹ کی قیمت دو ہزار کر دو", EditProduct),
        T("add a new kurti 1200", AddProduct),
        V("ایک نئی کرتی بارہ سو کی", AddProduct),

        // ---- chat or unclear (10%) ----
        T("assalam o alaikum", Chat),
        V("السلام علیکم، کیسے ہیں", Chat),
        T("shukriya", Chat),
        V("شکریہ بھائی", Chat),
        T("thank you", Chat),
        V("اچھا ٹھیک ہے، بس", Chat),
        T("mausam kaisa hai", Chat),
        V("آج موسم کیسا ہے", Chat),
        // A filler sound: asking what they mean, or a plain reply, are both fine.
        new("hmm", new[] { Clarify, Chat }, false),
        new("ہم م م", new[] { Clarify, Chat }, true),
    };

    // The fixed shop the mix is read against (names the eval cases use).
    private static readonly string[] ShopProducts = { "Shirt - 1800", "Kurti - 1200", "Shawl - 2500" };
    private static readonly string[] ShopCustomers = { "Sara Khan (03004445566, Model Town Lahore)", "Ali Raza (03215557788, Saddar Karachi)", "Bilal Ahmed (03001234567, Gulberg Lahore)" };
    private static readonly string[] ShopOrders = { "#3 Sara Khan Shipped", "#2 Ali Raza Pending", "#1 Sara Khan Pending" };

    [Fact]
    public async Task Traffic_mix_is_understood_by_the_real_model()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;
        var options = new OpenAiOptions { ApiKey = key };
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL_MODEL") is { Length: > 0 } model) options.Model = model;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var interpreter = new OpenAiTurnInterpreter(http, Options.Create(options), NullLogger<OpenAiTurnInterpreter>.Instance);

        var catalog = ShopProducts.Select(p => new AiCatalogItem { Name = p.Split(" - ")[0], Price = decimal.Parse(p.Split(" - ")[1]) }).ToList();
        var gate = new SemaphoreSlim(6);
        var results = await Task.WhenAll(Mix.Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var context = new TurnContext
                {
                    BusinessName = "Ayesha Collection", ShopType = "Clothing", CatalogSize = catalog.Count, Catalog = catalog,
                    KnownCustomers = ShopCustomers, RecentOrders = ShopOrders, FromVoice = row.Voice,
                };
                var action = await interpreter.UnderstandAsync(context, row.Message);
                var right = action is not null && row.Actions.Contains(action.Action);
                return (Row: row, Action: action, Right: right, Ms: clock.ElapsedMilliseconds);
            }
            finally { gate.Release(); }
        }));

        foreach (var r in results.Where(r => !r.Right))
            _output.WriteLine($"WRONG  [{(r.Row.Voice ? "voice" : "typed")}] \"{r.Row.Message}\"\n       wanted {string.Join(" / ", r.Row.Actions)}, got {r.Action?.Action ?? "nothing"}");
        var right = results.Count(r => r.Right);
        var times = results.Select(r => r.Ms).OrderBy(ms => ms).ToList();
        _output.WriteLine($"\n{options.Model}: {right}/{results.Length} right ({100.0 * right / results.Length:0}%); latency median {times[times.Count / 2]} ms, max {times[^1]} ms");
        Assert.True(right >= results.Length * PassRate, $"{right}/{results.Length} right, below {PassRate:P0}");
    }

    // Offline: the mix has the requested shape (about half voice, about 40% orders) and only real action names.
    [Fact]
    public void Mix_has_the_requested_shape()
    {
        Assert.All(Mix, r => Assert.All(r.Actions, a => Assert.Contains(a, TurnAction.All)));
        var voice = Mix.Count(r => r.Voice);
        Assert.InRange(voice, Mix.Length * 45 / 100, Mix.Length * 55 / 100);
        var orders = Mix.Count(r => r.Actions.Contains(Order));
        Assert.InRange(orders, Mix.Length * 35 / 100, Mix.Length * 45 / 100);
    }
}
