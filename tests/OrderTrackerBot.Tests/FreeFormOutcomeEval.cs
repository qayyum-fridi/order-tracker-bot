using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Ai;
using OrderTrackerBot.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace OrderTrackerBot.Tests;

/// <summary>
/// What a seller GETS when they ask in their own words to add, edit or delete a product, an order or a customer (Fixtures/free-form-cases.json,
/// 100 rows: Roman Urdu, Urdu script, English; about a third sent as voice notes). Each row runs through the whole bot on a fresh shop, with the
/// real model-first interpreter, and is scored on the outcome, not on the AI's label:
///   Done        the right change was made (after at most one YES), and nothing else changed
///   Asked       the bot asked a specific question (the right answer when a detail is missing)
///   DeadEnd     a generic "what I can do" / "samajh nahi aaya" / how-to reply, or nothing at all: the seller is stuck
///   WrongChange something changed that the seller did not ask for
/// Live: <c>OTB_FREEFORM_EVAL=1 OPENAI_API_KEY=... dotnet test --filter FreeFormOutcomeEval --logger "console;verbosity=detailed"</c>.
/// It never fails on a score (this is the baseline to improve); it fails only when every model call came back empty (a bad key).
/// Limits: messages written by us, not sellers; voice rows replay the text as the transcript (speech-to-text is not measured).
/// </summary>
public class FreeFormOutcomeEval
{
    private const string Phone = "923001234567";
    private readonly ITestOutputHelper _output;
    public FreeFormOutcomeEval(ITestOutputHelper output) => _output = output;

    internal sealed record Expect(string Kind, string? Name, decimal? Price, int? Id, string? Product, int? Qty, string? Status,
        string? Field, string? Value, string? Customer, string[] Items);

    internal sealed record Row(string Id, string Job, string Lang, bool Voice, string Text, bool Ask, Expect[] Expects);

    internal enum Verdict { Done, Asked, DeadEnd, WrongChange, Error }

    internal sealed record Outcome(Row Row, Verdict Verdict, bool NeededYes, bool OfferedTaps, string Replies, string? Note);

    internal static readonly string[] Jobs =
    {
        "add_product", "edit_product", "delete_product", "create_order", "edit_order", "cancel_order", "update_status",
        "add_customer", "edit_customer", "delete_customer"
    };

    private static readonly string[] Kinds =
    {
        "product_added", "product_price", "product_removed", "order_added", "order_qty", "order_has", "order_lacks", "order_status",
        "customer_added", "customer_field", "customer_removed"
    };

    // Production flags (appsettings.json), except the quick-bar buttons: they follow every reply and would make every answer look like it offered taps.
    private static FeatureOptions Features => new()
    {
        ModelFirst = "on", MvpOnly = true, ProductConfirmation = true, VoiceAck = true, SpokenReplies = false,
        ShortcutButtons = false, SuggestionButtons = false,
    };

    internal static List<Row> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "free-form-cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(e => new Row(
            e.GetProperty("id").GetString()!, e.GetProperty("job").GetString()!, e.GetProperty("lang").GetString()!,
            e.GetProperty("voice").GetBoolean(), e.GetProperty("text").GetString()!,
            e.TryGetProperty("ask", out var ask) && ask.GetBoolean(),
            e.TryGetProperty("expect", out var expect) ? expect.EnumerateArray().Select(x => new Expect(
                x.GetProperty("kind").GetString()!,
                Str(x, "name"), x.TryGetProperty("price", out var p) ? p.GetDecimal() : null, x.TryGetProperty("id", out var id) ? id.GetInt32() : null,
                Str(x, "product"), x.TryGetProperty("qty", out var q) ? q.GetInt32() : null, Str(x, "status"), Str(x, "field"), Str(x, "value"),
                Str(x, "customer"), x.TryGetProperty("items", out var items) ? items.EnumerateArray().Select(i => i.GetString()!).ToArray() : Array.Empty<string>()
            )).ToArray() : Array.Empty<Expect>())).ToList();

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
    }

    // ----- The shop every row starts from -----

    /// <summary>4 products, 4 customers (two called Ali, so "Ali" alone is ambiguous) and 3 orders: #1 Sara, #2 Bilal (both pending), #3 Ali Raza (shipped).</summary>
    private static async Task SeedAsync(AppDbContext db)
    {
        var seller = new Seller
        {
            WhatsAppPhoneNumber = Phone, BusinessName = "Ayesha Collection", BusinessType = "Clothing", OnboardingComplete = true,
            Session = new ConversationSession { State = ConversationState.Idle },
        };
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        var shirt = new Product { SellerId = seller.Id, Name = "Shirt", Price = 1800 };
        var lawn = new Product { SellerId = seller.Id, Name = "Lawn Suit", Price = 3500 };
        var dupatta = new Product { SellerId = seller.Id, Name = "Dupatta", Price = 600 };
        var kurti = new Product { SellerId = seller.Id, Name = "Kurti", Price = 1200 };
        db.Products.AddRange(shirt, lawn, dupatta, kurti);
        var sara = new Customer { SellerId = seller.Id, Name = "Sara Khan", Phone = "03004445566", Address = "Model Town", City = "Lahore" };
        var bilal = new Customer { SellerId = seller.Id, Name = "Bilal Ahmed", Phone = "03215557788", Address = "Gulberg", City = "Lahore" };
        var aliRaza = new Customer { SellerId = seller.Id, Name = "Ali Raza", Phone = "03331112233", Address = "Saddar", City = "Karachi" };
        var aliHassan = new Customer { SellerId = seller.Id, Name = "Ali Hassan", Phone = "03451234567", Address = "DHA", City = "Lahore" };
        db.Customers.AddRange(sara, bilal, aliRaza, aliHassan);
        await db.SaveChangesAsync();
        db.Orders.AddRange(
            OrderOf(seller, sara, OrderStatus.Pending, (shirt, 2)),
            OrderOf(seller, bilal, OrderStatus.Pending, (lawn, 1)),
            OrderOf(seller, aliRaza, OrderStatus.Shipped, (dupatta, 3)));
        await db.SaveChangesAsync();
    }

    private static Order OrderOf(Seller seller, Customer customer, OrderStatus status, params (Product Product, int Qty)[] lines)
    {
        var order = new Order { SellerId = seller.Id, CustomerId = customer.Id, Status = status, CreatedAt = DateTime.UtcNow.AddHours(-3) };
        foreach (var (product, qty) in lines)
            order.Items.Add(new OrderItem { ProductId = product.Id, ProductNameSnapshot = product.Name, UnitPrice = product.Price, Quantity = qty });
        order.Subtotal = order.Items.Sum(i => i.LineTotal);
        order.Total = order.Subtotal;
        return order;
    }

    /// <summary>Everything a seller could change, one line per row: products, customers, orders with their items.</summary>
    private static async Task<Dictionary<string, string>> SnapshotAsync(AppDbContext db)
    {
        var map = new Dictionary<string, string>();
        foreach (var p in await db.Products.AsNoTracking().ToListAsync())
            map[$"P{p.Id}"] = $"{p.Name}|{p.Price:0.##}|{p.IsActive}";
        foreach (var c in await db.Customers.AsNoTracking().ToListAsync())
            map[$"C{c.Id}"] = $"{c.Name}|{c.Phone}|{c.Address}|{c.City}|{c.DeletedAt is not null}";
        foreach (var o in await db.Orders.AsNoTracking().Include(o => o.Items).ToListAsync())
            map[$"O{o.Id}"] = $"{o.CustomerId}|{o.Status}|{string.Join(",", o.Items.OrderBy(i => i.ProductNameSnapshot).Select(i => $"{i.ProductNameSnapshot}x{i.Quantity}"))}";
        return map;
    }

    // ----- Running one row -----

    internal static async Task<Outcome> RunAsync(Row row, ITurnInterpreter interpreter)
    {
        using var factory = new TestDbContextFactory();
        using var db = factory.CreateContext();
        await SeedAsync(db);

        var sent = new List<string>();
        var labels = new List<string>();
        var taps = false;
        var sender = new Mock<IWhatsAppSender>();
        sender.Setup(s => s.SendTextMessageAsync(Phone, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, t, _) => sent.Add(t)).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendButtonsMessageAsync(Phone, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, body, b, _) => { sent.Add(body); labels.AddRange(b); taps = true; }).Returns(Task.CompletedTask);
        sender.Setup(s => s.SendListMessageAsync(Phone, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<MenuSection>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<MenuSection>, CancellationToken>((_, body, _, sections, _) => { sent.Add(body); labels.AddRange(sections.SelectMany(s => s.Rows).Select(r => r.Title)); taps = true; })
            .Returns(Task.CompletedTask);
        var older = new Mock<IAiOrderAssistant>();
        older.Setup(a => a.AnalyzeMessageAsync(It.IsAny<AiAnalysisContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiMessageAnalysis { Intent = "unclear", AiUnavailable = true });
        var media = new Mock<IWhatsAppMediaClient>();
        media.Setup(m => m.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((byte[] Bytes, string MimeType)?)(System.Text.Encoding.UTF8.GetBytes(row.Id + new string('.', 4096)), "audio/ogg"));
        var transcriber = new Mock<IAudioTranscriber>();
        transcriber.Setup(t => t.IsConfigured).Returns(true);
        transcriber.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(row.Text);

        ConversationEngine Engine() => new(db, older.Object, sender.Object, Mock.Of<IFounderAlertNotifier>(), media: media.Object,
            transcriber: transcriber.Object, turn: interpreter, features: Features);

        var before = await SnapshotAsync(db);
        var neededYes = false;
        try
        {
            if (row.Voice) await Engine().HandleAudioMessageAsync(Phone, "media-" + row.Id, "wamid-" + row.Id);
            else await Engine().HandleIncomingMessageAsync(Phone, row.Text, default);

            var first = sent.ToList();
            var firstTaps = taps;
            // A write waits for YES: give it, once, the way a seller taps the button. A question that is not a YES/NO is left unanswered.
            if (!row.Ask && !await AllMetAsync(row, db, before) && AsksYes(first, labels))
            {
                neededYes = true;
                await Engine().HandleIncomingMessageAsync(Phone, "yes", default);
            }

            var after = await SnapshotAsync(db);
            var changed = after.Keys.Union(before.Keys).Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || a != b).ToList();
            var unexpected = changed.Where(k => !Allowed(row, k, before, after)).ToList();
            var state = (await db.Sessions.AsNoTracking().SingleAsync()).State;
            var replies = string.Join("\n---\n", sent);
            var question = !IsGeneric(first) && (state != ConversationState.Idle || firstTaps || first.Any(m => m.Contains('?') || m.Contains('❓') || m.Contains('؟')));

            Verdict verdict;
            if (row.Ask) verdict = changed.Count > 0 ? Verdict.WrongChange : question ? Verdict.Asked : Verdict.DeadEnd;
            else if (await AllMetAsync(row, db, before) && unexpected.Count == 0) verdict = Verdict.Done;
            else if (changed.Count > 0) verdict = Verdict.WrongChange;
            else verdict = question ? Verdict.Asked : Verdict.DeadEnd;

            return new Outcome(row, verdict, neededYes, firstTaps, replies, unexpected.Count > 0 ? "changed: " + string.Join(", ", unexpected.Select(k => $"{k} {after.GetValueOrDefault(k) ?? "(gone)"}")) : null);
        }
        catch (Exception e)
        {
            return new Outcome(row, Verdict.Error, neededYes, taps, string.Join("\n---\n", sent), $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static bool AsksYes(IEnumerable<string> replies, IEnumerable<string> labels) =>
        replies.Any(r => r.Contains("YES")) || labels.Any(l => Regex.IsMatch(l, @"^\W*(yes|haan|han|save|ok)\b", RegexOptions.IgnoreCase));

    /// <summary>The replies that leave the seller stuck: the general "what I can do", "not understood", a how-to instead of doing it, the MVP cut.</summary>
    internal static bool IsGeneric(IReadOnlyList<string> replies)
    {
        if (replies.Count == 0) return true;
        return replies.Any(r => GenericPrefixes.Any(p => r.StartsWith(p, StringComparison.Ordinal)) || r.Contains("samajh nahi aaya", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] GenericPrefixes = new[]
        {
            "model-first.help", "model-first.scope", "model-first.unclear", "model-first.howto.addproduct", "model-first.howto.editproduct",
            "model-first.howto.showcatalog", "model-first.howto.addcustomer", "model-first.howto.editcustomer", "model-first.howto.showcustomer",
            "model-first.howto.createorder", "model-first.howto.updatestatus", "model-first.howto.cancelorder", "model-first.howto.showorders",
            "model-first.howto.showsummary", "model-first.howto.undo", "model-first.howto.other", "model-first.howto.addstock", "mvp.001",
        }
        .Select(k => MessageTemplates.Get(k).Split('{')[0].Trim())
        .Where(p => p.Length >= 12)
        .ToArray();

    // ----- Did the asked-for change happen? -----

    private static async Task<bool> AllMetAsync(Row row, AppDbContext db, Dictionary<string, string> before)
    {
        foreach (var e in row.Expects)
            if (!await MetAsync(e, db, before)) return false;
        return row.Expects.Length > 0;
    }

    private static async Task<bool> MetAsync(Expect e, AppDbContext db, Dictionary<string, string> before)
    {
        var products = await db.Products.AsNoTracking().ToListAsync();
        var customers = await db.Customers.AsNoTracking().ToListAsync();
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items).ToListAsync();
        bool Is(string? text, string? wanted) => text is not null && wanted is not null && wanted.Split('|').Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        Order? OrderNo(int? id) => orders.FirstOrDefault(o => o.Id == id);
        bool Has(Order? o, string? product) => o is not null && o.Items.Any(i => Is(i.ProductNameSnapshot, product));

        return e.Kind switch
        {
            "product_added" => products.Any(p => !before.ContainsKey($"P{p.Id}") && p.IsActive && Is(p.Name, e.Name) && (e.Price is null || p.Price == e.Price)),
            "product_price" => products.Any(p => Is(p.Name, e.Name) && p.Price == e.Price),
            "product_removed" => products.Where(p => Is(p.Name, e.Name)).All(p => !p.IsActive) && products.Any(p => Is(p.Name, e.Name)),
            "order_added" => orders.Any(o => !before.ContainsKey($"O{o.Id}") && Is(customers.FirstOrDefault(c => c.Id == o.CustomerId)?.Name, e.Customer)
                                             && e.Items.All(item => Has(o, item))),
            "order_qty" => OrderNo(e.Id)?.Items.Any(i => Is(i.ProductNameSnapshot, e.Product) && i.Quantity == e.Qty) == true,
            "order_has" => Has(OrderNo(e.Id), e.Product),
            "order_lacks" => OrderNo(e.Id) is { } o && !Has(o, e.Product),
            "order_status" => OrderNo(e.Id)?.Status.ToString() == e.Status,
            "customer_added" => customers.Any(c => !before.ContainsKey($"C{c.Id}") && c.DeletedAt is null && Is(c.Name, e.Name) && (e.Value is null || SamePhone(c.Phone, e.Value))),
            "customer_field" => customers.Any(c => Is(c.Name, e.Name) && e.Field switch
            {
                "phone" => SamePhone(c.Phone, e.Value),
                "address" => Is(c.Address, e.Value),
                "city" => Is(c.City, e.Value),
                _ => false,
            }),
            "customer_removed" => customers.Where(c => Is(c.Name, e.Name)).All(c => c.DeletedAt is not null) && customers.Any(c => Is(c.Name, e.Name)),
            _ => false,
        };
    }

    private static bool SamePhone(string? saved, string? wanted)
    {
        static string Local(string? p)
        {
            var digits = new string((p ?? "").Where(char.IsDigit).ToArray());
            return digits.StartsWith("92") && digits.Length == 12 ? "0" + digits[2..] : digits;
        }
        return Local(saved).Length > 0 && Local(saved) == Local(wanted);
    }

    /// <summary>A change this row asked for: the named product / customer / order, or a new one for an "added" row (a new order may bring a new customer).</summary>
    private static bool Allowed(Row row, string key, Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var isNew = !before.ContainsKey(key);
        var line = after.GetValueOrDefault(key) ?? before.GetValueOrDefault(key) ?? "";
        bool Named(string? wanted) => wanted is not null && wanted.Split('|').Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase));
        return row.Expects.Any(e => e.Kind switch
        {
            "product_added" => key.StartsWith('P') && isNew,
            "product_price" or "product_removed" => key.StartsWith('P') && Named(e.Name),
            "order_added" => (key.StartsWith('O') && isNew) || (key.StartsWith('C') && (isNew || Named(e.Customer))),
            "order_qty" or "order_has" or "order_lacks" or "order_status" => key == $"O{e.Id}",
            "customer_added" => key.StartsWith('C') && isNew,
            "customer_field" or "customer_removed" => key.StartsWith('C') && Named(e.Name),
            _ => false,
        });
    }

    // ----- Live run -----

    [Fact]
    public async Task Free_form_add_edit_delete_on_the_real_model()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("OTB_FREEFORM_EVAL") != "1" || string.IsNullOrWhiteSpace(key)) return;
        var options = new OpenAiOptions { ApiKey = key };
        if (Environment.GetEnvironmentVariable("OTB_TURN_EVAL_MODEL") is { Length: > 0 } model) options.Model = model;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var counting = new CountingInterpreter(new OpenAiTurnInterpreter(http, Options.Create(options), NullLogger<OpenAiTurnInterpreter>.Instance));

        var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(Load().Select(async row =>
        {
            await gate.WaitAsync();
            try { return await RunAsync(row, counting); }
            finally { gate.Release(); }
        }));

        Assert.False(counting.Calls > 0 && counting.Empty == counting.Calls,
            $"All {counting.Calls} model calls came back empty: the key, model name or network failed. No score was measured.");
        Report(options.Model, results);
    }

    private void Report(string model, Outcome[] results)
    {
        foreach (var r in results.Where(r => !Succeeded(r)).OrderBy(r => r.Verdict).ThenBy(r => r.Row.Id))
            _output.WriteLine($"{r.Verdict,-11} {r.Row.Id} [{r.Row.Job}/{r.Row.Lang}{(r.Row.Voice ? "/voice" : "")}] {r.Row.Text}" +
                              $"{(r.Note is null ? "" : $"\n            {r.Note}")}\n            bot: {Short(r.Replies)}");

        var n = results.Length;
        var ok = results.Count(Succeeded);
        var (low, high) = TurnFixtureEval.Wilson(ok, n);
        _output.WriteLine($"\n{model}: seller got what they needed in {ok}/{n} ({Pct(ok, n)}, 95% interval {low:P0}-{high:P0})");
        foreach (var v in Enum.GetValues<Verdict>())
            _output.WriteLine($"  {v,-11} {results.Count(r => r.Verdict == v)}");
        var asked = results.Where(r => r.Verdict == Verdict.Asked).ToList();
        _output.WriteLine($"  questions with tap choices: {asked.Count(r => r.OfferedTaps)}/{asked.Count}");
        _output.WriteLine($"  done rows that needed one YES: {results.Count(r => r.Verdict == Verdict.Done && r.NeededYes)}/{results.Count(r => r.Verdict == Verdict.Done)}");

        Group("job", results, r => r.Row.Job);
        Group("verb", results, r => r.Row.Job.Split('_')[0] switch { "create" or "add" => "add", "update" => "status", var v => v });
        Group("language", results, r => r.Row.Lang);
        Group("channel", results, r => r.Row.Voice ? "voice" : "typed");
        Group("detail", results, r => r.Row.Ask ? "missing (should ask)" : "complete (should do)");
    }

    /// <summary>Success: a complete request was done; a request missing a detail got a question.</summary>
    internal static bool Succeeded(Outcome r) => r.Row.Ask ? r.Verdict == Verdict.Asked : r.Verdict == Verdict.Done;

    private void Group(string title, Outcome[] results, Func<Outcome, string> key)
    {
        _output.WriteLine($"by {title}:");
        foreach (var g in results.GroupBy(key).OrderBy(g => g.Key))
            _output.WriteLine($"  {g.Key,-22} {g.Count(Succeeded)}/{g.Count()} ({Pct(g.Count(Succeeded), g.Count())})   dead ends {g.Count(r => r.Verdict == Verdict.DeadEnd)}");
    }

    private static string Short(string text) => text.Replace("\n", " ").Length <= 160 ? text.Replace("\n", " ") : text.Replace("\n", " ")[..160] + "…";

    private static string Pct(int right, int total) => $"{100.0 * right / Math.Max(1, total):F0}%";

    private sealed class CountingInterpreter(ITurnInterpreter inner) : ITurnInterpreter
    {
        private int _calls, _empty;
        public int Calls => _calls;
        public int Empty => _empty;

        public async Task<TurnAction?> UnderstandAsync(TurnContext context, string text, AiImageInput? image = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var action = await inner.UnderstandAsync(context, text, image, cancellationToken);
            if (action is null) Interlocked.Increment(ref _empty);
            return action;
        }
    }

    // ----- Offline: no key needed -----

    [Fact]
    public void Fixture_has_100_rows_covering_every_job_language_and_voice()
    {
        var rows = Load();
        Assert.Equal(100, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.All(rows, r => Assert.Contains(r.Job, Jobs));
        Assert.All(rows, r => Assert.Contains(r.Lang, new[] { "roman", "urdu", "english" }));
        // A row either asks for a change (and says which) or is missing a detail (and expects a question), never both.
        Assert.All(rows, r => Assert.True(r.Ask ^ r.Expects.Length > 0, r.Id));
        Assert.All(rows.SelectMany(r => r.Expects), e => Assert.Contains(e.Kind, Kinds));
        foreach (var job in Jobs) Assert.True(rows.Count(r => r.Job == job) >= 5, $"{job} has fewer than 5 rows");
        Assert.True(rows.Count(r => r.Lang == "urdu") >= 20, "fewer than 20 Urdu-script rows");
        Assert.True(rows.Count(r => r.Voice) >= 30, "fewer than 30 voice rows");
        Assert.True(rows.Count(r => r.Ask) >= 15, "fewer than 15 missing-detail rows");
    }

    [Fact]
    public async Task Scorer_counts_a_confirmed_change_as_done()
    {
        var row = Load().Single(r => r.Id == "F019"); // "lawn suit ab 3800 ka bikega"
        var model = new FixedInterpreter(new TurnAction { Action = TurnAction.EditProduct, Products = { new AiNewProduct { Name = "Lawn Suit", Price = 3800 } } });

        var outcome = await RunAsync(row, model);

        Assert.True(outcome.Verdict == Verdict.Done, $"{outcome.Verdict}: {outcome.Note} | {outcome.Replies}");
        Assert.True(outcome.NeededYes);
    }

    [Fact]
    public async Task Scorer_counts_the_generic_reply_as_a_dead_end()
    {
        var row = Load().Single(r => r.Id == "F026"); // "Dupatta ab nahi bechti, list se hata do"
        var outcome = await RunAsync(row, new FixedInterpreter(new TurnAction { Action = TurnAction.HelpOrChat }));

        Assert.True(outcome.Verdict == Verdict.DeadEnd, $"{outcome.Verdict}: {outcome.Replies}");
    }

    [Fact]
    public async Task Scorer_counts_a_question_as_asked_on_a_missing_detail_row()
    {
        var row = Load().Single(r => r.Id == "F024"); // "Shirt ka rate badalna hai"
        var outcome = await RunAsync(row, new FixedInterpreter(new TurnAction { Action = TurnAction.Clarify, Question = "Shirt ki nayi qeemat kya hai?" }));

        Assert.True(outcome.Verdict == Verdict.Asked, $"{outcome.Verdict}: {outcome.Replies}");
    }

    /// <summary>
    /// The floor with the model unavailable (every call returns nothing): only exact typed commands can work. Printed as a baseline; the test
    /// fails only if a row crashes the bot, since a seller must get a reply even when the AI is down.
    /// </summary>
    [Fact]
    public async Task Without_the_model_the_bot_never_crashes_and_the_floor_is_printed()
    {
        var results = new List<Outcome>();
        foreach (var row in Load()) results.Add(await RunAsync(row, new FixedInterpreter(null)));

        foreach (var r in results.Where(Succeeded)) _output.WriteLine($"works without AI: {r.Row.Id} {r.Verdict} | {r.Row.Text}");
        foreach (var r in results.Where(r => r.Verdict == Verdict.WrongChange))
            _output.WriteLine($"WRONG CHANGE without AI: {r.Row.Id} | {r.Row.Text}\n    {r.Note}\n    bot: {Short(r.Replies)}");
        _output.WriteLine($"without the model: {results.Count(Succeeded)}/{results.Count} got what they needed; dead ends {results.Count(r => r.Verdict == Verdict.DeadEnd)}; " +
                          $"wrong changes {results.Count(r => r.Verdict == Verdict.WrongChange)}");
        Assert.Empty(results.Where(r => r.Verdict == Verdict.Error).Select(r => $"{r.Row.Id}: {r.Note}"));
    }

    private sealed class FixedInterpreter(TurnAction? action) : ITurnInterpreter
    {
        public Task<TurnAction?> UnderstandAsync(TurnContext context, string text, AiImageInput? image = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<TurnAction?>(action);
    }
}
