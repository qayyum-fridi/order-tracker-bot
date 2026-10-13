using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>Runs only with OTB_SIM=1 (OTB_SIM_SEED=<n> to repeat a run).</summary>
public sealed class SimFactAttribute : FactAttribute
{
    public SimFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OTB_SIM") != "1")
            Skip = "Set OTB_SIM=1 to run the real-shop simulation (OTB_SIM_SEED=<n> repeats a run).";
    }
}

/// <summary>
/// A made-up but realistic shop day driven through the real <see cref="ConversationEngine"/>: real onboarding, ten products (some typed wrongly),
/// orders with typos and missing details, a discount, shipping, mistakes after shipping, and reports. It prints what the seller typed, what the
/// bot answered and how that was classified. Only the AI is replaced: a small stub that extracts "Name 2 Lawn Suit aur 1 Dupatta, 0300-1234567" perfectly,
/// so this measures the engine's flow and its handling of mistakes, NOT how well the real model understands language (that needs a real OpenAI key).
/// Nothing here fails on a wrong answer: the report is the result. It only fails if the engine throws.
/// </summary>
public class RealShopSimulationTests : IDisposable
{
    private const string Phone = "923001234567";
    private readonly ITestOutputHelper _out;
    private readonly TestDbContextFactory _dbFactory = new();
    private readonly Mock<IAiOrderAssistant> _ai = new();
    private readonly Mock<IWhatsAppSender> _sender = new();
    private readonly List<string> _replies = new();
    private readonly StringBuilder _report = new();
    private readonly List<(string Kind, string Said)> _results = new();
    private Random _rng = new(42);
    private HashSet<string> _catalogNames = new(StringComparer.OrdinalIgnoreCase);

    public RealShopSimulationTests(ITestOutputHelper output)
    {
        _out = output;
        _sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => _replies.Add(text)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, _, _) => _replies.Add(body)).Returns(Task.CompletedTask);
        _sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, _, _) => _replies.Add(body)).Returns(Task.CompletedTask);
        _ai.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiAnalysisContext ctx, string text, CancellationToken _) => StubAnalyse(ctx, text));
    }

    // ---- stub AI: perfect on "Name 2 Lawn Suit aur 1 Dupatta, 0300-1234567, Lahore"; "unclear" on everything else --------------------------------------
    private static readonly Regex PhoneRx = new(@"(?:\+?92|0)3\d{2}[-\s]?\d{7}");
    private static readonly Regex ItemRx = new(@"(\d+)\s+([A-Za-z][A-Za-z ]*?)(?=\s+aur\s|\s*,|\s*$)", RegexOptions.IgnoreCase);

    private static AiMessageAnalysis StubAnalyse(AiAnalysisContext ctx, string text)
    {
        var items = ItemRx.Matches(text).Select(m => (Qty: int.Parse(m.Groups[1].Value), Name: m.Groups[2].Value.Trim()))
            .Where(i => !PhoneRx.IsMatch(i.Name)).ToList();
        if (items.Count == 0) return new AiMessageAnalysis { Intent = "unclear" };
        var phone = PhoneRx.Match(text);
        var order = new AiOrderDraft
        {
            CustomerName = text.Split(' ', ',')[0],
            Phone = phone.Success ? phone.Value : null,
            DiscountCode = text.Contains("EID10", StringComparison.OrdinalIgnoreCase) ? "EID10" : null,
        };
        foreach (var (qty, name) in items)
        {
            var match = ctx.Catalog.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            order.Items.Add(new AiOrderItemDraft { ProductName = name, Quantity = qty, MatchedCatalogProductName = match?.Name });
        }
        return new AiMessageAnalysis { Intent = "new_order", IsOrderAttempt = true, Order = order };
    }

    private static string Classify(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return "SILENT";
        if (reply.Contains("samajh nahi aaya", StringComparison.OrdinalIgnoreCase) || reply.Contains("Maazrat", StringComparison.OrdinalIgnoreCase)) return "NOT UNDERSTOOD";
        if (reply.Contains("nahi mila", StringComparison.OrdinalIgnoreCase) || reply.Contains("nahi hua", StringComparison.OrdinalIgnoreCase)) return "REFUSED / NOT FOUND";
        if (reply.Contains('?') || reply.Contains("Reply", StringComparison.OrdinalIgnoreCase)) return "ASKED";
        if (reply.Contains('✅')) return "DONE";
        return "ANSWERED";
    }

    private async Task<string> SayAsync(ConversationEngine engine, string kind, string text)
    {
        _replies.Clear();
        await engine.HandleIncomingMessageAsync(Phone, text, default);
        var reply = string.Join("\n---\n", _replies);
        var verdict = Classify(reply);
        _results.Add((kind + " | " + verdict, text));
        _report.AppendLine($"[{kind}] SELLER: {text}");
        _report.AppendLine($"        BOT ({verdict}): {Shorten(reply)}");
        return reply;
    }

    private static string Shorten(string text)
    {
        var flat = text.Replace("\n", " ⏎ ");
        return flat.Length > 220 ? flat[..220] + "…" : flat;
    }

    // ---- a seller who answers whatever the bot is waiting for, like a real person would ----------------------------------------------------------------
    private async Task FinishOrderAsync(ConversationEngine engine, AppDbContext db, string productFix, string phone)
    {
        for (var turn = 0; turn < 8; turn++)
        {
            db.ChangeTracker.Clear();
            var session = await db.Sessions.AsNoTracking().FirstAsync();
            var ctx = SessionContextData.FromJson(session.ContextJson);
            string? answer = session.State switch
            {
                ConversationState.AwaitingOrderConfirmation => "yes",
                ConversationState.AwaitingDuplicateOrderConfirmation => "1",
                ConversationState.AwaitingOrderGroupingChoice => "2",
                ConversationState.AwaitingOrderMissingFields => ctx.PendingMissingField switch
                {
                    "ProductChoice" => $"2 {productFix}",              // "that was a typo of an existing product"
                    "MappedProductName" => productFix,
                    "NewProductPrice" => "1500",
                    "Phone" => phone,
                    "CustomerName" => "Customer",
                    "CustomerMatch" => _rng.Next(2) == 0 ? "1" : "2",
                    _ => null
                },
                _ => null
            };
            if (answer is null) return;
            await SayAsync(engine, "follow-up", answer);
        }
    }

    private static string Typo(Random rng, string name)
    {
        if (name.Length < 4) return name;
        var i = rng.Next(1, name.Length - 2);
        var chars = name.ToCharArray();
        (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
        return new string(chars);
    }

    [SimFact]
    public async Task RealShop_FullDay_WithMistakes_PrintsReport()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("OTB_SIM_SEED"), out var s) ? s : 42;
        _rng = new Random(seed);
        _report.AppendLine($"=== Real shop simulation, seed {seed} ===");

        using var db = _dbFactory.CreateContext();
        var engine = new ConversationEngine(db, _ai.Object, _sender.Object, Mock.Of<IFounderAlertNotifier>());

        // ---- 1. onboarding, then ten products typed the way sellers type them, with mistakes -------------------------------------------------------
        foreach (var step in new[] { "start", "Roman Urdu", "Setup shuru karein", "Bismillah Cloth House", "skip", "10 ke qareeb" })
            await SayAsync(engine, "onboarding", step);

        var products = new List<(string Say, string Name, bool Mistake)>
        {
            ("Lawn Suit - 3500", "Lawn Suit", false),
            ("Kurti - 1800", "Kurti", false),
            ("Dupatta 800", "Dupatta", true),                              // no dash
            ("Chadar - Rs. 2,200", "Chadar", true),                        // Rs and a thousands comma
            ("Khussa - 2500 add kar dein", "Khussa", true),                // trailing instruction
            ("Silk Suit ki price 4800 rakho", "Silk Suit", true),          // a sentence, not "naam - price"
            ("Shawl - teen hazar do sau", "Shawl", true),                  // price in words
            ("Handbag - abc", "Handbag", true),                            // not a number: must NOT be added
            ("Sandal - 1500", "Sandal", false),
            ("Polo Shirt - 500, cost 300, stock 10", "Polo Shirt", false),
            ("Scarf -", "Scarf", true),                                    // no price: must NOT be added
            ("Hoodie - 2800", "Hoodie", false),
        };
        foreach (var line in products.OrderBy(_ => _rng.Next()))
            await SayAsync(engine, line.Mistake ? "product (mistake)" : "product", line.Say);
        await SayAsync(engine, "onboarding", "done");

        db.ChangeTracker.Clear();
        _catalogNames = (await db.Products.Select(p => p.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _report.AppendLine($"--- catalog after the product phase ({_catalogNames.Count}): {string.Join(", ", _catalogNames.OrderBy(n => n))}");

        // ---- 2. a discount, then real-looking orders (with typos / missing phone / the same order twice) -------------------------------------------
        await SayAsync(engine, "discount", "create discount: EID10 10%");

        var customers = new[] { ("Ayesha", "0300-1111111"), ("Bilal", "0301-2222222"), ("Hina", "0302-3333333"), ("Usman", "0303-4444444"), ("Sara", "0304-5555555"),
            ("Ali", "0305-6666666"), ("Zainab", "0306-7777777"), ("Hamza", "0307-8888888") };
        var usable = _catalogNames.Where(n => n is not "Handbag" and not "Scarf").OrderBy(_ => _rng.Next()).ToList();
        var placed = 0;
        foreach (var (name, phone) in customers.OrderBy(_ => _rng.Next()))
        {
            var a = usable[_rng.Next(usable.Count)];
            var b = usable[_rng.Next(usable.Count)];
            var qty1 = _rng.Next(1, 4);
            var typoThis = _rng.Next(3) == 0;
            var shown = typoThis ? Typo(_rng, a) : a;
            var noPhone = _rng.Next(4) == 0;
            var code = _rng.Next(4) == 0 ? " EID10" : "";
            var text = $"{name} {qty1} {shown} aur 1 {b}{(noPhone ? "" : ", " + phone)}{code}";
            var kind = typoThis || noPhone ? "order (mistake)" : "order";
            await SayAsync(engine, kind, text);
            await FinishOrderAsync(engine, db, a, phone);
            placed++;
            if (_rng.Next(5) == 0)
            {
                await SayAsync(engine, "order (same again)", text);     // the seller sends the identical order twice
                await FinishOrderAsync(engine, db, a, phone);
            }
        }

        // ---- 3. shipping, with the mistakes sellers make --------------------------------------------------------------------------------------------
        db.ChangeTracker.Clear();
        var orderIds = await db.Orders.OrderBy(o => o.Id).Select(o => o.Id).ToListAsync();
        foreach (var id in orderIds.Take(Math.Max(1, orderIds.Count / 2)))
            await SayAsync(engine, "ship", $"mark {id} shipped");
        if (orderIds.Count > 0)
        {
            await SayAsync(engine, "ship (mistake)", $"mark {orderIds[0]} shiped");                       // typo
            await SayAsync(engine, "ship (mistake)", "mark 999 shipped");                                   // no such order
            await SayAsync(engine, "tracking", $"add tracking: {orderIds[0]}, TCS123456");
            await SayAsync(engine, "after shipping (mistake)", $"cancel {orderIds[0]}");                    // already shipped: should be locked / confirmed
            await SayAsync(engine, "after shipping (mistake)", $"mark {orderIds[^1]} delivered");           // delivered before it was shipped
            await SayAsync(engine, "payment", $"order {orderIds[0]} advance 500");
        }

        // ---- 4. questions and reports, one phrased badly ---------------------------------------------------------------------------------------------
        foreach (var q in new[] { "orders today", "pending orders", "today's summary", "catalog", "Ali ka order", "Ali ka order khol do", "Ali koi customer hai",
                     "expense 500 packaging", "monthly net", "profit", "stock Kurti 20", "stock", "undo", "kuch bhi bakwas likh diya" })
            await SayAsync(engine, q.Contains("bakwas") || q.Contains("khol") || q.Contains("koi customer") ? "question (loose phrasing)" : "question", q);

        // ---- 5. what the database ended up with --------------------------------------------------------------------------------------------------------
        db.ChangeTracker.Clear();
        _report.AppendLine();
        _report.AppendLine("=== Final state ===");
        _report.AppendLine($"products: {await db.Products.CountAsync()}   customers: {await db.Customers.CountAsync()}   orders: {await db.Orders.CountAsync()}");
        var byStatus = await db.Orders.GroupBy(o => o.Status).Select(grp => new { Status = grp.Key, Count = grp.Count() }).ToListAsync();
        foreach (var entry in byStatus)
            _report.AppendLine($"  {entry.Status}: {entry.Count}");
        var sharedPhones = await db.Customers.Where(c => c.Phone != null).GroupBy(c => c.Phone).CountAsync(grp => grp.Count() > 1);
        _report.AppendLine($"customers with more than one record for the same phone: {sharedPhones}");
        var mustNotAdd = products.Where(item => item.Say.Contains("abc") || item.Say.EndsWith("-")).ToList();
        foreach (var item in mustNotAdd)
            _report.AppendLine($"  must NOT be in the catalog: {item.Name} -> {(_catalogNames.Contains(item.Name) ? "WAS ADDED (bad)" : "not added (good)")}");
        foreach (var item in products.Where(item2 => item2.Mistake).Except(mustNotAdd))
            _report.AppendLine($"  mistaken product line: {item.Say,-34} -> {(_catalogNames.Contains(item.Name) ? "added" : "NOT ADDED")}");

        _report.AppendLine();
        _report.AppendLine("=== Summary by kind (how the bot reacted) ===");
        foreach (var grp in _results.GroupBy(res => res.Kind).OrderBy(grp2 => grp2.Key))
            _report.AppendLine($"  {grp.Key,-48} {grp.Count()}");
        var notUnderstood = _results.Where(res => res.Kind.EndsWith("NOT UNDERSTOOD")).ToList();
        _report.AppendLine($"=== {notUnderstood.Count} message(s) answered 'not understood' ===");
        foreach (var res in notUnderstood) _report.AppendLine($"  {res.Kind.Split(" | ")[0]}: {res.Said}");

        _out.WriteLine(_report.ToString());
        var file = Environment.GetEnvironmentVariable("OTB_SIM_REPORT");
        if (!string.IsNullOrWhiteSpace(file)) await File.WriteAllTextAsync(file, _report.ToString());
    }

    public void Dispose() => _dbFactory.Dispose();
}
