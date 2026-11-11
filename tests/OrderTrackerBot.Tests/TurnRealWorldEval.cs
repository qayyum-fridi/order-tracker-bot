using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Infrastructure.Ai;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// Hard cases that real sellers send and the mix sets do not cover: speech-to-text mistakes, several requests in one message, edits to an
/// order's items, typos, missing names, emoji and half-finished sentences. Written by hand; the expected answers are the sensible reading.
/// Skipped unless OTB_TURN_EVAL=1 and OPENAI_API_KEY are set:
/// <c>OTB_TURN_EVAL=1 OPENAI_API_KEY=... dotnet test --filter TurnRealWorldEval --logger "console;verbosity=detailed"</c>
/// </summary>
public class TurnRealWorldEval
{
    private const double PassRate = 0.90;
    private readonly ITestOutputHelper _output;
    public TurnRealWorldEval(ITestOutputHelper output) => _output = output;

    private const string Order = TurnAction.CreateOrder, Status = TurnAction.UpdateStatus, Cancel = TurnAction.CancelOrder,
        Summary = TurnAction.ShowSummary, Catalog = TurnAction.ShowCatalog, AddProduct = TurnAction.AddProduct, EditProduct = TurnAction.EditProduct,
        Customer = TurnAction.ShowCustomer, EditOrder = TurnAction.EditOrder, Clarify = TurnAction.Clarify, Chat = TurnAction.HelpOrChat,
        ShowOrder = TurnAction.ShowOrder;

    /// <summary>One case: the message, the answers the model may give for its first action, whether it is a voice transcript, and how many actions it must carry.</summary>
    internal sealed record Case(string Message, string[] Actions, bool Voice = false, int MinActions = 1);

    private static Case T(string m, params string[] a) => new(m, a);
    private static Case V(string m, params string[] a) => new(m, a, Voice: true);
    private static Case M(string m, int min, params string[] a) => new(m, a, MinActions: min);

    internal static readonly Case[] Cases =
    {
        // ---- speech-to-text mistakes: misheard numbers, names and words ----
        V("Sara ko do shirt bhej do, teen sau ka", Order),
        V("Ali ka order saat kurti, phone zero teen do aik", Order),
        V("ordar teen shipped kar do", Status),
        V("سارہ کو پانچ شرٹ بھیجیں ڈیلیوری ڈیڑھ سو", Order),
        V("کل کا حساب بتاؤ جی", Summary),
        V("Ali ko saath kurti bhej do", Order),
        V("catelog dikha do", Catalog),
        V("Sara ka nambar batao", Customer),
        V("teen number ka order cancel karo", Cancel),

        // ---- several requests in one message: the first action must come first and carry the rest ----
        M("Ali ka order bhej do aur Sara ka number batao", 2, Status, Customer),
        M("catalog dikhao aur aaj ka hisaab bhi", 2, Catalog, Summary),
        M("Shirt ka rate 2000 karo aur kal ka hisaab batao", 2, EditProduct, Summary),
        M("Sara ko 2 shirt bhej do aur order 3 cancel kar do", 2, Order, Cancel),
        M("ek naya product shawl 2500 aur order 1 shipped", 2, AddProduct, Status),
        V("catalog dikha do aur order teen ka status batao", Catalog),

        // ---- edits to an order's items ----
        T("order 3 mein shirt ki quantity 3 kar do", EditOrder),
        V("تین نمبر آرڈر میں کرتی ایک کم کر دو", EditOrder),
        T("Ali ke order se shirt nikal do", EditOrder),
        T("order 2 mein kurti ka rate 1000 kar do", EditOrder),
        V("دو نمبر آرڈر میں ایک شال اور ڈالو", EditOrder),
        T("order 1 me 2 shirt add karo", EditOrder),

        // ---- typos ----
        T("Sra ko 2 shrt bhej do", Order),
        T("odr 3 shiped kar do", Status),
        T("cathalog dikhao", Catalog),
        T("Bilal ko 2 shwal bhejo", Order),
        T("kitna bikaa aaj", Summary),

        // ---- missing names: ask who, do not guess ----
        T("2 shirt bhej do cod", Order, Clarify),
        V("do kurti bhej do", Order, Clarify),
        T("address kya hai", Customer, Clarify),

        // ---- emoji: ignored, the words decide ----
        T("Sara ko 2 shirt bhej do 📦", Order),
        T("order 3 delivered ✅", Status),
        T("catalog 📋 dikhao", Catalog),
        V("👍 ok", Chat, Clarify),
        T("🙏 shukriya", Chat),

        // ---- half-finished sentences: ask for the rest ----
        T("Sara ko", Order, Clarify, Customer),
        T("order 3 ka", ShowOrder, Clarify),
        V("kal ke liye", Order, Clarify),
        T("shirt 1800 wali", AddProduct, Order, Clarify),
        T("order cancel", Cancel, Clarify),
    };

    private static readonly string[] ShopProducts = { "Shirt - 1800", "Kurti - 1200", "Shawl - 2500" };
    private static readonly string[] ShopCustomers = { "Sara Khan (03004445566, Model Town Lahore)", "Ali Raza (03215557788, Saddar Karachi)", "Bilal Ahmed (03001234567, Gulberg Lahore)" };
    private static readonly string[] ShopOrders = { "#3 Sara Khan Shipped", "#2 Ali Raza Pending", "#1 Sara Khan Pending" };

    [Fact]
    public async Task Real_world_hard_cases_are_understood_by_the_real_model()
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
        _output.WriteLine($"\n{options.Model} real-world: {right}/{results.Length} right ({100.0 * right / results.Length:0}%)");
        Assert.True(right >= results.Length * PassRate, $"{right}/{results.Length} right, below {PassRate:P0}");
    }

    // Offline: every expected answer is a real action, and the multi-request cases ask for more than one action.
    [Fact]
    public void Real_world_cases_name_real_actions_and_multi_cases_ask_for_several()
    {
        Assert.All(Cases, c => Assert.All(c.Actions, a => Assert.Contains(a, TurnAction.All)));
        Assert.Contains(Cases, c => c.MinActions >= 2);
        Assert.All(Cases.Where(c => c.MinActions >= 2), c => Assert.True(c.Actions.Length >= 1));
    }
}
