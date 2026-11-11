using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// A held-out check of 500 sentences written AFTER the prompt was tuned, never used to tune it (TurnMixEval was). Generated from fixed
/// templates with a fixed seed, so the same set comes out every run. Half are voice transcripts; Roman Urdu, Urdu script and English.
/// Skipped unless OTB_TURN_EVAL=1 and OPENAI_API_KEY are set:
/// <c>OTB_TURN_EVAL=1 OPENAI_API_KEY=... dotnet test --filter TurnHeldOutEval --logger "console;verbosity=detailed"</c>
/// </summary>
public class TurnHeldOutEval
{
    private const double PassRate = 0.95;
    private readonly ITestOutputHelper _output;
    public TurnHeldOutEval(ITestOutputHelper output) => _output = output;

    private const string Order = TurnAction.CreateOrder, Status = TurnAction.UpdateStatus, Cancel = TurnAction.CancelOrder,
        ShowOrders = TurnAction.ShowOrders, Summary = TurnAction.ShowSummary, Catalog = TurnAction.ShowCatalog,
        AddProduct = TurnAction.AddProduct, EditProduct = TurnAction.EditProduct, Customer = TurnAction.ShowCustomer, Clarify = TurnAction.Clarify,
        Chat = TurnAction.HelpOrChat, Out = TurnAction.OutOfScope, HowTo = TurnAction.HowTo;

    internal sealed record Row(string Message, string[] Actions, bool Voice);

    private static readonly string[] Names = { "Zainab", "Fatima", "Usman", "Nadia", "Bilal", "Sana", "Zara", "Ahmed", "Hina", "Ali", "Sara", "Imran" };
    private static readonly string[] UrduNames = { "فاطمہ", "عثمان", "نادیہ", "بلال", "سانیہ", "زارا", "احمد", "حنا", "علی", "سارہ", "عمران", "یاسمین" };
    private static readonly string[] Products = { "shirt", "kurti", "shawl", "dupatta", "lawn suit", "bag", "chappal" };
    private static readonly string[] UrduProducts = { "شرٹ", "کرتی", "شال", "دوپٹہ", "لان سوٹ", "بیگ", "چپل" };
    private static readonly string[] UrduCounts = { "ایک", "دو", "تین", "چار", "پانچ" };
    private static readonly string[] Amounts = { "500", "1000", "2000", "750" };

    private static readonly string[] RomanOrder =
    {
        "{C} ko {N} {P} bhej do", "{C} ke liye {N} {P} likh do", "{N} {P} {C} ko, cod", "order: {C}, {N} {P}, delivery 150",
        "new order {C} {N} {P}", "{C} ka order {N} {P}", "send {N} {P} to {C}", "order for {C}: {N} {P}", "{C} ko {N} {P} aur 1 dupatta bhej dein",
    };
    private static readonly string[] UrduOrder = { "{C} کو {N} {P} بھیج دیں", "{C} کے لیے {N} {P} لکھ دو", "{C} کو {N} {P} چاہئیں" };
    private static readonly string[] RomanStatus = { "order {N} shipped kar do", "order {N} delivered kar do", "order {N} wapas aa gaya", "order {N} pahuch gaya" };
    private static readonly string[] RomanCustomerStatus = { "{C} ka order shipped ho gaya", "{C} ka order deliver ho gaya", "{C} ka order wapas aa gaya" };
    private static readonly string[] UrduStatus = { "{N} نمبر آرڈر پہنچ گیا", "{N} نمبر آرڈر بھیج دیا", "{N} نمبر آرڈر واپس آ گیا" };
    private static readonly string[] RomanCancel = { "order {N} cancel karo", "order {N} cancel kar do" };
    private static readonly string[] UrduCancel = { "{N} نمبر آرڈر منسوخ کر دو", "{N} نمبر آرڈر کینسل کر دیں" };
    private static readonly string[] RomanPayment = { "{C} ne {M} de diye", "order {N} advance {M}", "{C} se {M} mil gaye, payment mark karo" };
    private static readonly string[] UrduPayment = { "{C} نے {M} دے دیے", "{N} نمبر آرڈر پر {M} ایڈوانس لے لیے" };
    private static readonly string[] RomanSummary = { "aaj kitna bika", "kal ka hisaab batao", "aaj ki sale kitni hui" };
    private static readonly string[] UrduSummary = { "آج کتنی سیل ہوئی", "کل کا حساب بتائیں" };
    private static readonly string[] RomanCustomer = { "{C} ka number kya hai", "{C} kahan rehta hai", "{C} ka phone number bata do" };
    private static readonly string[] UrduCustomer = { "{CU} کا نمبر کیا ہے", "{CU} کہاں رہتی ہے" };
    private static readonly string[] RomanOrders = { "pending orders dikhao", "kitne pending orders hain", "is hafte kitne orders aaye" };
    private static readonly string[] UrduOrders = { "پینڈنگ آرڈر دکھاؤ", "کتنے آرڈر پینڈنگ ہیں" };
    private static readonly string[] RomanHowTo = { "order kaise cancel karte hain", "product kaise add karun", "customer kaise add karte hain" };
    private static readonly string[] UrduHowTo = { "کسٹمر کیسے ایڈ کرتے ہیں", "آرڈر کیسے ڈیلیٹ کرتے ہیں" };
    private static readonly string[] RomanCatalog = { "catalog dikhao", "meri products ki list", "catalog dikhao please" };
    private static readonly string[] UrduCatalog = { "کیٹلاگ دکھاؤ", "میری پروڈکٹس دکھاؤ" };
    private static readonly string[] RomanNewProduct = { "naya product {P} {M} ka", "{P} daal do, {M} ka hai" };
    private static readonly string[] UrduNewProduct = { "نیا پروڈکٹ {UP} {M} کا", "{UP} ڈالو، قیمت {M}" };
    private static readonly string[] RomanRate = { "{P} ka rate {M} karo", "{P} ki qeemat {M} kar do" };
    private static readonly string[] UrduRate = { "{UP} کی قیمت {M} کر دو" };
    private static readonly string[] RomanChat = { "salam bhai", "shukriya", "good morning", "kya haal hai", "thanks bhai" };
    private static readonly string[] UrduChat = { "جی ٹھیک ہے شکریہ", "شکریہ بھائی", "السلام علیکم" };
    private static readonly string[] Unclear = { "ok", "hmm", "abc xyz" };
    private static readonly string[] UrduUnclear = { "ہمم", "اوکے" };

    private static T Pick<T>(Random r, T[] items) => items[r.Next(items.Length)];

    /// <summary>500 sentences: 200 orders, 100 status and payment, 100 questions, 50 catalog, 50 chat or unclear. Every other one is a voice transcript.</summary>
    internal static readonly Row[] Set = Build();

    private static Row[] Build()
    {
        var rnd = new Random(20260111);
        var rows = new List<Row>();
        void Add(string message, params string[] actions) => rows.Add(new Row(message, actions, rows.Count % 2 == 1));

        for (var i = 0; i < 200; i++)
        {
            var c = rnd.Next(Names.Length);
            var p = rnd.Next(Products.Length);
            var n = rnd.Next(1, 6);
            if (i % 3 == 0)
                Add(Pick(rnd, UrduOrder).Replace("{C}", UrduNames[c]).Replace("{N}", UrduCounts[n - 1]).Replace("{P}", UrduProducts[p]), Order);
            else
                Add(Pick(rnd, RomanOrder).Replace("{C}", Names[c]).Replace("{N}", n.ToString()).Replace("{P}", Products[p]), Order);
        }

        for (var i = 0; i < 100; i++)
        {
            var c = rnd.Next(Names.Length);
            var n = rnd.Next(1, 20);
            var m = Pick(rnd, Amounts);
            switch (i % 5)
            {
                case 0: Add(Pick(rnd, RomanStatus).Replace("{N}", n.ToString()), Status); break;
                case 1: Add(Pick(rnd, RomanCustomerStatus).Replace("{C}", Names[c]), Status); break;
                case 2: Add(Pick(rnd, UrduStatus).Replace("{N}", UrduCounts[n % 5]), Status); break;
                case 3: Add(Pick(rnd, RomanCancel).Replace("{N}", n.ToString()), Cancel); break;
                default: Add(Pick(rnd, RomanPayment).Replace("{C}", Names[c]).Replace("{N}", n.ToString()).Replace("{M}", m), Out, Clarify); break;
            }
        }

        for (var i = 0; i < 100; i++)
        {
            var c = rnd.Next(Names.Length);
            switch (i % 6)
            {
                case 0: Add(Pick(rnd, RomanSummary), Summary); break;
                case 1: Add(Pick(rnd, UrduSummary), Summary); break;
                case 2: Add(Pick(rnd, RomanCustomer).Replace("{C}", Names[c]), Customer); break;
                case 3: Add(Pick(rnd, UrduCustomer).Replace("{CU}", UrduNames[c]), Customer); break;
                case 4: Add(Pick(rnd, RomanOrders), ShowOrders); break;
                default: Add(Pick(rnd, rnd.Next(2) == 0 ? RomanHowTo : UrduHowTo), HowTo); break;
            }
        }

        for (var i = 0; i < 50; i++)
        {
            var p = rnd.Next(Products.Length);
            var m = Pick(rnd, Amounts);
            switch (i % 5)
            {
                case 0: Add(Pick(rnd, RomanCatalog), Catalog); break;
                case 1: Add(Pick(rnd, UrduCatalog), Catalog); break;
                case 2: Add(Pick(rnd, RomanNewProduct).Replace("{P}", Products[p]).Replace("{M}", m), AddProduct); break;
                case 3: Add(Pick(rnd, UrduNewProduct).Replace("{UP}", UrduProducts[p]).Replace("{M}", m), AddProduct); break;
                default: Add(Pick(rnd, RomanRate).Replace("{P}", Products[p]).Replace("{M}", m), EditProduct); break;
            }
        }

        for (var i = 0; i < 50; i++)
        {
            switch (i % 4)
            {
                case 0: Add(Pick(rnd, RomanChat), Chat); break;
                case 1: Add(Pick(rnd, UrduChat), Chat); break;
                case 2: Add(Pick(rnd, Unclear), Clarify, Chat); break;
                default: Add(Pick(rnd, UrduUnclear), Clarify, Chat); break;
            }
        }

        return rows.ToArray();
    }

    private static readonly string[] ShopProducts = { "Shirt - 1800", "Kurti - 1200", "Shawl - 2500" };
    private static readonly string[] ShopCustomers = { "Sara Khan (03004445566, Model Town Lahore)", "Ali Raza (03215557788, Saddar Karachi)", "Bilal Ahmed (03001234567, Gulberg Lahore)" };
    private static readonly string[] ShopOrders = { "#3 Sara Khan Shipped", "#2 Ali Raza Pending", "#1 Sara Khan Pending" };

    [Fact]
    public async Task Held_out_traffic_is_understood_by_the_real_model()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;
        var options = new OpenAiOptions { ApiKey = key };
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL_MODEL") is { Length: > 0 } model) options.Model = model;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var interpreter = new OpenAiTurnInterpreter(http, Options.Create(options), NullLogger<OpenAiTurnInterpreter>.Instance);

        var catalog = ShopProducts.Select(p => new AiCatalogItem { Name = p.Split(" - ")[0], Price = decimal.Parse(p.Split(" - ")[1]) }).ToList();
        var gate = new SemaphoreSlim(6);
        var results = await Task.WhenAll(Set.Select(async row =>
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
        _output.WriteLine($"\n{options.Model} held-out: {right}/{results.Length} right ({100.0 * right / results.Length:0}%); latency median {times[times.Count / 2]} ms, max {times[^1]} ms");
        Assert.True(right >= results.Length * PassRate, $"{right}/{results.Length} right, below {PassRate:P0}");
    }

    // Offline: 500 sentences, every expected answer is a real action, and half are voice transcripts.
    [Fact]
    public void Held_out_set_has_500_sentences_naming_real_actions_and_is_half_voice()
    {
        Assert.Equal(500, Set.Length);
        Assert.All(Set, r => Assert.All(r.Actions, a => Assert.Contains(a, TurnAction.All)));
        Assert.Equal(250, Set.Count(r => r.Voice));
    }
}
